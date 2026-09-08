---
title: 28 EF Core Query 與常見 Methods
tags: [ef-core, sql-server, dotnet, linq, iqueryable]
---

# 28 EF Core：從 LINQ 到 SQL Server

## 學習目標

- 建立 `DbContext`、`DbSet<T>`、`IQueryable<T>` 的角色分工。
- 能選擇常見 async query / write method。
- 看懂 C# → LINQ → EF Core → SQL Server 的執行邊界。

## 1. 一句話理解

EF Core 是 ORM 與 LINQ provider：它把 model、identity、change tracking 與 query translation 接到資料庫，但 `IQueryable` query 仍要到 terminal operation 才執行 SQL。

## 2. 實際 SQL

C#：

```csharp
var query = db.Users
    .Where(x => x.IsActive)
    .OrderBy(x => x.Name)
    .Select(x => new UserListItem(x.Id, x.Name));

var result = await query.ToListAsync(cancellationToken);
```

大致可能翻成：

```sql
SELECT [u].[Id], [u].[Name]
FROM [Users] AS [u]
WHERE [u].[IsActive] = CAST(1 AS bit)
ORDER BY [u].[Name];
```

實際 SQL 會依 provider、model naming、parameter、版本與 query shape 變化；用 logging 或 `ToQueryString()` 驗證，不要把上面當固定輸出。

## 3. 執行結果

### Core objects

| EF Core | 角色 |
| --- | --- |
| `DbContext` | unit of work、identity map、change tracker、query / save 的 database session abstraction |
| `DbSet<User>` | `User` entity 的 query root 與 add / update / remove 入口；通常實作 `IQueryable<User>` |
| `IQueryable<User>` | 尚未執行的 provider query，可繼續組合 LINQ |
| `SaveChangesAsync()` | 偵測 tracked changes、產生 INSERT / UPDATE / DELETE、執行 transaction / commands |

### 常見 query method

```csharp
var first = await db.Users.FirstAsync(cancellationToken);
var maybeFirst = await db.Users.FirstOrDefaultAsync(cancellationToken);

var one = await db.Users.SingleAsync(
    x => x.Email == email, cancellationToken);
var maybeOne = await db.Users.SingleOrDefaultAsync(
    x => x.Email == email, cancellationToken);

var exists = await db.Users.AnyAsync(
    x => x.Email == email, cancellationToken);
var count = await db.Users.CountAsync(
    x => x.IsActive, cancellationToken);
var list = await db.Users.ToListAsync(cancellationToken);
```

- `FirstAsync`：沒有 row 就 exception；只要求至少一筆。
- `FirstOrDefaultAsync`：沒有 row 回 default / null；多筆只取第一筆。
- `SingleAsync`：要求剛好一筆；0 或多筆都 exception。
- `SingleOrDefaultAsync`：允許 0 筆，但多筆 exception；適合 database unique invariant。
- `AnyAsync`：通常表達 existence，不要用 `CountAsync() > 0` 取代它。
- `CountAsync`：要求 count，可能需要計算多筆；只問是否存在用 `AnyAsync`。

## 4. SQL Server 背後大概做什麼

### `FindAsync` vs predicate query

```csharp
var user = await db.Users.FindAsync(
    [id], cancellationToken);
```

`FindAsync` 適合「已知 primary key」的單一 entity lookup：先檢查同一個 `DbContext` 是否已 tracking 這個 key，若已有就直接回傳 tracked instance；沒有才查資料庫。它不是任意 predicate query 的替代品。

```csharp
var user = await db.Users.SingleOrDefaultAsync(
    x => x.Email == email, cancellationToken);
```

email 查詢要用 predicate；若 domain 要求唯一，資料庫也應有 unique constraint / index，不能只相信 application code。

### Write methods

```csharp
db.Users.Add(newUser);       // state = Added
db.Users.Update(existing);   // 通常標為 Modified；要小心整個 graph / 所有欄位
db.Users.Remove(existing);   // state = Deleted

await db.SaveChangesAsync(cancellationToken);
```

`Add` / `Update` / `Remove` 多半先改變 change tracker state；真正 command 通常在 `SaveChangesAsync` 執行。`Update` 不是「只更新你改的 property」的通用保證；對 detached graph 可能標記大量欄位，部分更新可用先 query tracked entity 再設欄位，或明確 attach / mark property modified。

## 5. 與 C# / ASP.NET Core 的關聯

典型讀取 service：

```csharp
public async Task<IReadOnlyList<UserResponse>> GetActiveAsync(
    CancellationToken cancellationToken)
{
    return await db.Users
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderBy(x => x.Name)
        .Select(x => new UserResponse(
            x.Id,
            x.Name,
            x.CreatedAt))
        .ToListAsync(cancellationToken);
}
```

這裡有幾個 boundary：

```text
IQueryable<User>
  ↓ Where / OrderBy / Select 組 expression tree
  ↓ ToListAsync(cancellationToken)
SQL Server 執行 SQL
  ↓ materialize DTO list
IReadOnlyList<UserResponse>
```

`DbContext` 通常註冊 scoped，與一次 HTTP request / unit of work 對齊；不要把同一個 context 當成 thread-safe singleton 使用。

## 6. 常見誤區

- `DbSet<T>` 不是已經載入的 list；它是 query root。
- `FirstOrDefaultAsync` 不會驗證只有一筆；唯一性要用 `Single...` + database constraint 表達。
- `FindAsync` 只適合 primary key lookup，且可能直接回 tracked entity，不會重新查 DB。
- `Add` / `Update` 不等於已寫進 DB，通常要 `SaveChangesAsync`。
- 在 `Select` 前 `ToListAsync` 會把 projection / filter 後移到 memory。

## 7. 面試回答

> `DbContext` 是 unit of work 與 change tracker，`DbSet<T>` 是 entity query root。EF Core 的 LINQ 先組成 `IQueryable` expression tree，`ToListAsync`、`FirstAsync`、`AnyAsync` 等 terminal operation 才通常轉 SQL 執行。`FirstOrDefault` 允許多筆只取第一筆；`SingleOrDefault` 允許 0 筆但多筆會錯，適合 unique invariant。`FindAsync` 以 primary key 為主，會先查同一 context 的 tracked entity。

## 8. 小練習

1. 將「查 email 唯一 user」分別用 `FirstOrDefaultAsync` 和 `SingleOrDefaultAsync` 寫出，說明選擇。
2. 指出 `ToListAsync` 放在 projection 前後會有什麼差別。
3. 何時用 `FindAsync`，何時用 `SingleOrDefaultAsync`？
