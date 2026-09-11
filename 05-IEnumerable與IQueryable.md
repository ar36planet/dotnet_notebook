---
title: 05 IEnumerable 與 IQueryable
tags: [csharp, linq, ef-core, sql]
---

# 05 `IEnumerable<T>` vs `IQueryable<T>`

## 學習目標

- 分辨 LINQ to Objects 與 LINQ to Entities。
- 知道 expression tree、deferred execution、`ToList` / `ToListAsync` 的位置。
- 避免 EF Core 常見的 N+1、過早 materialization 與 client-side filtering 問題。

## 1. 一句話理解

`IEnumerable<T>` 的 query 由 .NET 在記憶體中執行；`IQueryable<T>` 把 query 表達成 provider 可以解析的 expression tree，EF Core 通常把它翻成 SQL，在 `ToListAsync()` 等 terminal operation 才送到資料庫。

## 2. 先看會出事的地方

訂單表只有三筆時，下面兩段看起來一樣；資料量變成三十萬筆時，第二段會先把整張表拉回應用程式：

```csharp
// 條件留在資料庫
var pendingNumbersFromDb = db.Orders
    .Where(order => !order.Shipped)
    .Select(order => order.Number)
    .ToList();

// 先查完整資料，再在記憶體篩選
var pendingNumbersFromMemory = db.Orders
    .ToList()
    .Where(order => !order.Shipped)
    .Select(order => order.Number)
    .ToList();
```

`ToList()` 是兩個世界的交界：在它之前，`IQueryable<T>` 還能讓 EF Core 組合並翻譯查詢；在它之後，資料已經是記憶體中的 `List<T>`，後續 LINQ 在應用程式執行。

## 3. C# 語法

### `IEnumerable<T>`：LINQ to Objects

```csharp
IEnumerable<User> users = GetUsersFromMemory();

var result = users
    .Where(x => x.IsActive)
    .Select(x => x.Name)
    .ToList(); // predicate / projection 在目前 process 執行
```

lambda 在這裡通常轉成 `Func<User, bool>` / `Func<User, string>`；`Enumerable.Where` 會逐筆呼叫 delegate。

### `IQueryable<T>`：provider 可解析的 query

```csharp
IQueryable<User> query = dbContext.Users
    .Where(x => x.IsActive);

var activeNames = await query
    .OrderBy(x => x.Name)
    .Select(x => x.Name)
    .ToListAsync(cancellationToken);
```

對 `IQueryable<T>`，lambda 可能被轉成 `Expression<Func<User, bool>>`。EF Core 解析 expression tree，依 provider 把可翻譯部分轉成 SQL。

## 4. 實務範例：同一張訂單表，三種寫法送出的 SQL

以下三段跑在 EF Core 10 + SQLite，SQL 是實際 log 出來的。

**條件留在資料庫**

```csharp
var pending = db.Orders
    .Where(o => !o.Shipped)
    .Select(o => o.Number)
    .ToList();
```

```sql
SELECT "o"."Number"
FROM "Orders" AS "o"
WHERE NOT ("o"."Shipped")
```

篩選和欄位都在資料庫做完，回來的只有需要的那一欄。

**條件寫成 C# method**

```csharp
static class OrderRules
{
    public static bool IsBig(Order o) => o.Amount > 1000m;
}

db.Orders.Where(o => OrderRules.IsBig(o)).ToList();
```

```text
The LINQ expression 'DbSet<Order>()
    .Where(o => OrderRules.IsBig(o))' could not be translated. Either rewrite the
query in a form that can be translated, or switch to client evaluation explicitly
by inserting a call to 'AsEnumerable', 'AsAsyncEnumerable', 'ToList', or 'ToListAsync'.
```

EF Core 拿到的是 expression tree，看不進 `IsBig` 的方法內容，翻不出 SQL 就丟 `InvalidOperationException`。條件寫回 lambda（`o => o.Amount > 1000m`）就翻得出來。同樣的邏輯如果寫成 local function，連編譯都不會過：`CS8110 運算式樹狀目錄不可包含區域函式的參考`。

**先 `ToList()` 再篩**

```csharp
db.Orders.ToList().Where(o => !o.Shipped).ToList();
```

```sql
SELECT "o"."Id", "o"."Amount", "o"."Number", "o"."Shipped"
FROM "Orders" AS "o"
```

沒有 `WHERE`，整張表拉回記憶體，篩選在 C# 做。三筆資料看不出差別，三十萬筆就是另一回事。`ToList()` 放在哪一行，就是 `IQueryable` 與 `IEnumerable` 的交界。

### 什麼時候回到 memory？

```csharp
var users = await _context.Users
    .Where(x => x.IsActive)
    .ToListAsync(cancellationToken); // SQL 已執行，users 是 List<User>

var result = users
    .Where(x => IsSpecialName(x.Name)) // 這裡是 LINQ to Objects
    .ToList();
```

明確呼叫 `AsEnumerable()` 也會把後續 pipeline 切到 `IEnumerable<T>`：

```csharp
var result = _context.Users
    .Where(x => x.IsActive) // 嘗試翻 SQL
    .AsEnumerable()
    .Where(x => IsSpecialName(x.Name)) // 在 memory 執行
    .ToList();
```

使用前要問：你是「不得不使用只能在 C# 執行的邏輯」，還是只是沒查清楚 EF Core 能否翻譯？能在 SQL 做的 filter / projection，通常應留在 `IQueryable` 階段。

### 常見效能問題

```csharp
// 不好：只要 name，卻先載入完整 entity。
var names = (await _context.Users.ToListAsync(cancellationToken))
    .Select(x => x.Name)
    .ToList();

// 較好：只投影需要的 column。
var names = await _context.Users
    .Select(x => x.Name)
    .ToListAsync(cancellationToken);
```

其他常見問題：

- 在 loop 裡對每個 parent 另外查 child，產生 N+1 queries。
- 過早 `ToList()`，讓後續 filter / sort 在 application memory 執行。
- 在不確定 translation 的地方加入自訂 method，導致 exception 或意外 client evaluation。
- 沒有 pagination，對大 table 直接 `ToListAsync()`。
- 把 `IQueryable<T>` 從 repository 洩漏到太多層，讓上層任意組 query，造成 transaction、tracking、效能責任不清楚。

## 5. 常見誤解

- `IQueryable<T>` 不代表「一定是 database」；它只是一個 provider query abstraction。
- 寫了 `Where` 不代表已查 DB；`ToListAsync`、`FirstAsync`、`AnyAsync` 等才常是 execution boundary。
- `ToListAsync` 不是在任何 `IEnumerable<T>` 都有；它通常是 EF Core / async LINQ provider 的 extension。
- `IEnumerable<T>` 並非永遠是快照；可能是 lazy iterator，每次列舉都重新工作。
- `AsEnumerable()` 不會把資料自動縮小；若放得太早，反而把大量資料拉到 memory。

## 6. 面試怎麼回答

> `IEnumerable<T>` 代表在 application memory 中列舉，LINQ to Objects 會直接執行 delegates。`IQueryable<T>` 讓 provider 取得 expression tree，EF Core 可以把 query 翻成 SQL。兩者通常都是 deferred execution，直到 `ToListAsync`、`FirstAsync` 或 `AnyAsync` 才真正查詢。效能上我會把 filter、projection、pagination 盡量留在 `IQueryable`，避免過早 `ToList`、N+1 和把不必要的欄位載入記憶體。

## 7. 小練習

1. 判斷 `db.Users.Where(...).ToListAsync()` 中哪一行觸發 DB。
2. 把「先查所有 user 再 filter」改成 SQL-friendly query。
3. 解釋 `AsEnumerable()` 為什麼是重要的執行位置切換點。
