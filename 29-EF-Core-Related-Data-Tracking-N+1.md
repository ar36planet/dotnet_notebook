---
title: 29 EF Core Related Data Tracking N+1
tags: [ef-core, sql-server, n-plus-one, tracking, include]
---

# 29 EF Core：Related Data、N+1 與 Tracking

## 學習目標

- 看懂 `Include` / `ThenInclude`、projection 與 split query 的 trade-off。
- 找出 N+1 query，改成可控的 SQL shape。
- 判斷 read-only query 是否適合 `AsNoTracking()`。

## 1. 一句話理解

關聯資料怎麼載入、是否追蹤，以及產生幾條 SQL，必須在查詢時決定；否則容易出現 N+1、重複資料與記憶體成本。

## 2. 實際 SQL

### N+1 的危險形狀

```csharp
var users = await db.Users.ToListAsync(cancellationToken);

foreach (var user in users)
{
    // 若 lazy loading / 另外查詢，每一位 user 可能各發一條 SQL。
    var orders = await db.Orders
        .Where(x => x.UserId == user.Id)
        .ToListAsync(cancellationToken);
}
```

概念 round trips：

```text
1 次 SELECT Users
+ N 次 SELECT Orders WHERE UserId = ...
= N+1 queries
```

把 `DbContextOptionsBuilder.LogTo(Console.WriteLine, LogLevel.Information)` 開在測試環境，計算 `Microsoft.EntityFrameworkCore.Database.Command` 行數；N+1 應看到 1 條 users command + N 條 orders command，`Include`／projection／`AsSplitQuery` 則比較各自的 SQL 與 command count。沒有固定 provider、版本與 seed data 時，不把 SQL 或「只有一條 query」寫成實測結果。

### Eager loading

```csharp
var users = await db.Users
    .Include(x => x.Orders)
    .ThenInclude(x => x.Items)
    .ToListAsync(cancellationToken);
```

`Include`／`ThenInclude` 是 eager loading。`Orders.ThenInclude(Items)` 是巢狀 collection，不會造成同層 collection 的 cartesian explosion；真正的 cross product 例子是同時 `Include(x => x.Orders).Include(x => x.Roles)`。單一 collection 仍可能重複 user 欄位，但那是 data duplication。EF Core 預設是 single-query mode，不會自動替每個查詢選 split query。

三種 loading 方式的前提：

| 方式 | 何時發 SQL | 必要設定／風險 |
| --- | --- | --- |
| eager | 初始 query | `Include`／projection；同層 collections 可能產生 row multiplication |
| explicit | 呼叫 `Entry(...).Collection(...).LoadAsync()` 時 | 完全由程式控制，迴圈內呼叫仍會形成 N+1 |
| lazy | 存取 navigation 時 | 不是 POCO 預設行為；需 proxies + `UseLazyLoadingProxies()` 且 navigation `virtual`，容易 N+1 |

explicit loading 範例：

```csharp
var user = await db.Users.SingleAsync(user => user.Id == userId, cancellationToken);
await db.Entry(user).Collection(user => user.Orders).LoadAsync(cancellationToken);
```

lazy proxy 需要 `Microsoft.EntityFrameworkCore.Proxies`、`options.UseLazyLoadingProxies()`，以及可覆寫的 `virtual` navigation；`ILazyLoader` 是另一條明確注入路徑。

## 3. 執行結果

### Projection 通常更適合 API read model

```csharp
var result = await db.Users
    .Select(u => new UserWithOrderCount(
        u.Id,
        u.Name,
        u.Orders.Count,
        u.Orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(5)
            .Select(o => new OrderSummary(o.Id, o.TotalAmount))
            .ToList()))
    .ToListAsync(cancellationToken);
```

結果是 DTO shape，不必載入 entity graph 的所有欄位，也不會因為只需要 count 就把所有 orders materialize 到 application。

這個 DTO-only projection 本來就不會追蹤 entity，`AsNoTracking()` 在此是冗餘的；若 projection 同時帶出 entity instance，該 entity 仍可能被 tracking。

### Tracking vs no tracking

| 查詢 | Change tracker | 典型用途 |
| --- | --- | --- |
| default tracking | 追蹤 entity、做 identity resolution、偵測修改 | 查 entity 後修改並 SaveChanges |
| `AsNoTracking()` | 不追蹤、不做一般 identity resolution | read-only API、列表、report、DTO query |
| `AsNoTrackingWithIdentityResolution()` | 不掛到 context，但在 query materialization 期間去重 instance | 需要 graph identity，但不需要保存修改 |

```csharp
var readOnlyUsers = await db.Users
    .AsNoTracking()
    .Where(x => x.IsActive)
    .ToListAsync(cancellationToken);
```

## 4. SQL Server 背後大概做什麼

### `Include` 與 projection 的取捨

- `Include` 要求 entity graph；EF Core 可能使用 JOIN 或 split queries。
- projection 只要求你在 `Select` 裡表達的 columns / nested result，通常更貼近 API DTO。
- `AsSplitQuery()` 可以把某些 collection include 拆成多條 SQL，減少 cartesian explosion，但多 query 之間的一致性與 round trips 也要考慮。

```csharp
var users = await db.Users
    .AsSplitQuery()
    .Include(x => x.Orders)
    .ToListAsync(cancellationToken);
```

Split query 不是「零成本修復」：它可能減少 row multiplication，也可能在 concurrent update 下需要更仔細的一致性判斷。依 query shape 與 actual SQL / plan 決定。

Filtered Include（EF Core 5+）在 tracking query 中可能被 relationship fix-up 回填先前已追蹤、但不符合 filter 的 entities；該 navigation 也會被視為已載入。要隔離這個行為，使用新的短生命週期 `DbContext`、`AsNoTracking()` 或直接投影 DTO。

版本備註：lazy-loading proxies 自 EF Core 2.1；Filtered Include、split query、`AsNoTrackingWithIdentityResolution()` 自 EF Core 5；EF Core 8 支援 untracked entity 的 lazy／explicit loading；EF Core 10 修正 split query 搭配 `Skip`／`Take` 時各 SQL ordering 的一致性問題。

### Change tracker

EF Core 預設使用 snapshot change tracking；entity 被追蹤時保存 property values 與 key identity，讓：

```csharp
var user = await db.Users.SingleAsync(x => x.Id == id, cancellationToken);
user.Name = "New name";
await db.SaveChangesAsync(cancellationToken);
```

可以產生針對性 UPDATE。若是 `AsNoTracking` 查回來再 attach，會失去部分原始值與 identity context，可能反而更複雜；不要把 no-tracking 當成所有 query 的預設修復。

## 5. 與 C# / ASP.NET Core 的關聯

API read endpoint 的常見策略：

```csharp
[HttpGet("{id:guid}/orders")]
public async Task<ActionResult<IReadOnlyList<OrderDto>>> GetOrders(
    Guid id,
    CancellationToken cancellationToken)
{
    var orders = await db.Orders
        .AsNoTracking()
        .Where(x => x.UserId == id)
        .OrderByDescending(x => x.CreatedAt)
        .Select(x => new OrderDto(
            x.Id,
            x.TotalAmount,
            x.CreatedAt))
        .ToListAsync(cancellationToken);

    return Ok(orders);
}
```

這比「先抓 users，再 foreach 每個 user 查 orders」更可預期。若需要 user + order nested JSON，可以一次 projection；若需要 entity graph 修改，才考慮 tracking + Include。

## 6. 常見誤區

- `Include` 不會自動解決所有 N+1；lazy loading、explicit loading 或 loop query 仍可能產生 N+1。
- `AsNoTracking()` 不會讓 SQL 本身變快；它主要避免 context 追蹤與 identity work，SQL / I/O 仍要看 query。
- `Include` 與 projection 不是單純 coding style 差異，而是 entity graph vs read model 的不同 contract。
- `AsSplitQuery()` 可能減少 cartesian explosion，也可能增加 round trips 與 consistency complexity。
- N+1 的問題不是「foreach 本身不能用」，而是 foreach 裡每次都觸發 DB query。

## 7. 面試回答

> N+1 是先查 1 次 parent，再對每個 parent 各查一次 child，產生大量 DB round trips。改善方式依需求選 projection、明確 Include、批次查詢或 split query。read-only API 通常使用 `AsNoTracking` 或直接 projection 成 DTO，以減少 change tracker overhead；需要修改並 SaveChanges 時則使用 tracking entity。Include 載入 graph 很方便，但要觀察 join row multiplication，不能盲目使用。

## 8. 小練習

1. 找出一段 foreach 裡查 orders 的 N+1，改成單一 projection。
2. 什麼情況應用 `AsNoTracking`？什麼情況不應用？
3. 比較 Include 與 projection 對 API response 的差別。
