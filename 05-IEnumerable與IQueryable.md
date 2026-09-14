---
title: 05 IEnumerable 與 IQueryable
tags: [csharp, linq, ef-core, sql]
---

# 05 `IEnumerable<T>` vs `IQueryable<T>`

## 學習目標

- 分辨 LINQ to Objects 與 LINQ to Entities。
- 知道 expression tree、延遲執行、`ToList`／`ToListAsync` 的位置。
- 避免 EF Core 常見的 N+1、過早具體化與 client evaluation 問題。

## 1. 一句話理解

`IEnumerable<T>` 的查詢由 .NET 在記憶體中執行；`IQueryable<T>` 把查詢表達成 provider 可以解析的 expression tree，關聯式 EF Core provider 會把可翻譯部分轉成 SQL，在 `ToListAsync()`、`foreach` 等列舉結果的操作才送到資料庫。

## 2. Java 對照

Java 的 `list.stream().filter(...).toList()` 對應 C# 的 LINQ to Objects；兩者都在記憶體處理資料，但 Java Stream 只能走訪一次，`IEnumerable<T>` 的查詢變數可以重複列舉。JPA Criteria API、JPQL 或 Spring Data `Specification` 都是由 provider 組出資料庫查詢，概念上接近 `IQueryable<T>`；C# lambda 在 `Queryable` overload 中可以被編成 expression tree，Java Stream lambda 本身不會自動變成可供 JPA provider 解析的 expression tree。

| Java | C# | 執行位置 |
| --- | --- | --- |
| `stream().filter(...).map(...)` | `IEnumerable<T>.Where(...).Select(...)` | 記憶體 |
| `getResultList()` | `ToList()`／`ToListAsync()` | 具體化結果 |
| JPA Criteria／Specification | `IQueryable<T>` + `Expression<Func<T, bool>>` | 由 provider 翻譯；關聯式 provider 會送 SQL |

## 3. C# 語法

### 先看會出事的地方

訂單結果先被具體化，再篩選時，篩選會在應用程式執行：

```csharp
var pendingNumbersFromMemory = db.Orders
    .ToList()
    .Where(order => !order.Shipped)
    .Select(order => order.Number)
    .ToList();
```

`ToList()` 是兩個世界的交界：它之前的查詢仍可由 EF Core 組合並翻譯；它之後已是記憶體中的 `List<T>`，後續 LINQ 在應用程式執行。三筆資料看不出差別，三十萬筆時傳輸與記憶體成本就不同。

### `IEnumerable<T>`：LINQ to Objects

```csharp
IEnumerable<Order> orders =
[
    new(1001, "A001", 1200m, Shipped: false),
    new(1002, "A002", 890m, Shipped: true),
];

var result = orders
    .Where(order => !order.Shipped)
    .Select(order => order.Number)
    .ToList(); // 條件與投影在目前 process 執行

public sealed record Order(int Id, string Number, decimal Amount, bool Shipped);
```

`Enumerable.Where` 收的是 `Func<Order, bool>`，`Enumerable.Select` 收的是 `Func<Order, string>`；編譯器會把這裡的 lambda 編成委派，`Enumerable.Where` 再逐筆呼叫它。

### `IQueryable<T>`：provider 可解析的 query

以下片段位於可取得 `DbContext db` 的 `async` 方法中。

```csharp
IQueryable<Order> query = db.Orders
    .Where(order => !order.Shipped);

var pendingNumbers = await query
    .OrderBy(order => order.Number)
    .Select(order => order.Number)
    .ToListAsync(cancellationToken);
```

`Queryable.Where` 收的是 `Expression<Func<Order, bool>>`；編譯器會把 lambda 編成 expression tree，交給 `IQueryProvider.CreateQuery`。EF Core 解析 expression tree，依 provider 把可翻譯部分轉成 SQL。這是由 `Where` 接收端的靜態型別決定，不是執行期猜測。

## 4. 實務範例：同一張訂單表，三種寫法送出的 SQL

以下查詢跑在 EF Core 10.0.x + SQLite，SQL 是實際 log 出來的。

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

**把 delegate 變數傳進 `Where`**

```csharp
Func<Order, bool> pendingPredicate = order => !order.Shipped;
var pendingOrders = db.Orders
    .Where(pendingPredicate)
    .ToList();
```

這裡選到的是 `Enumerable.Where`，因為 `pendingPredicate` 是 `Func<Order, bool>`，不是 expression tree。`IQueryable<T>` 同時也是 `IEnumerable<T>`，所以程式可以編譯，但條件會在記憶體執行。

實際 SQL：

```sql
SELECT "o"."Id", "o"."Amount", "o"."Number", "o"."Shipped"
FROM "Orders" AS "o"
```

**條件寫成 C# method**

```csharp
static class OrderRules
{
    public static bool IsBig(Order o) => o.Amount > 1000m;
}

db.Orders.Where(o => OrderRules.IsBig(o)).ToList();
```

```text
System.InvalidOperationException: The LINQ expression 'DbSet<Order>()
    .Where(o => OrderRules.IsBig(o))' could not be translated. Either rewrite the
query in a form that can be translated, or switch to client evaluation explicitly
by inserting a call to 'AsEnumerable', 'AsAsyncEnumerable', 'ToList', or 'ToListAsync'. See https://go.microsoft.com/fwlink/?linkid=2101038 for more information.
```

這個例外是在 `ToList()` 執行查詢、嘗試翻譯 SQL 時拋出，不是在呼叫 `Where()` 時拋出。

EF Core 拿到的是 expression tree，看不進 `IsBig` 的方法內容；在 `Where` 這種非最上層投影的位置翻不出 SQL，就在 `ToList()` 執行時丟 `InvalidOperationException`。條件寫回 lambda（`o => o.Amount > 1000m`）就翻得出來。同樣的邏輯如果寫成 local function，連編譯都不會過：`CS8110 運算式樹狀目錄不可包含區域函式的參考`。

EF Core 3.0 起只放寬最上層 `Select` 投影的 client evaluation。下面的 `IsBig` 不在 `Where`，所以查詢不會丟例外；EF Core 會先把計算 `Big` 所需的欄位取回，再在 client 端執行方法：

```csharp
var projected = db.Orders
    .Where(order => !order.Shipped)
    .Select(order => new
    {
        order.Number,
        Big = OrderRules.IsBig(order)
    })
    .ToList();
```

實際 SQL：

```sql
SELECT "o"."Number", "o"."Id", "o"."Amount", "o"."Shipped"
FROM "Orders" AS "o"
WHERE NOT ("o"."Shipped")
```

**先 `ToList()` 再篩**

```csharp
db.Orders.ToList().Where(o => !o.Shipped).ToList();
```

```sql
SELECT "o"."Id", "o"."Amount", "o"."Number", "o"."Shipped"
FROM "Orders" AS "o"
```

沒有 `WHERE`，整張表拉回記憶體，篩選在 C# 做；`ToList()` 放在這裡後，後續型別已經是 `IEnumerable<Order>`。

### 什麼時候回到記憶體？

以下片段接續前面的 `db`、`Order` 與 `OrderRules`。

查詢執行後，後續 LINQ 會在記憶體中處理：

```csharp
var pendingOrders = await db.Orders
    .Where(order => !order.Shipped)
    .ToListAsync(cancellationToken); // SQL 已執行，結果是 List<Order>

var bigPendingOrders = pendingOrders
    .Where(OrderRules.IsBig)
    .ToList(); // 這裡是 LINQ to Objects
```

明確呼叫 `AsEnumerable()` 也會把後續查詢切到 `IEnumerable<T>`。這裡只有前一個 `Where` 進 SQL，`OrderRules.IsBig` 在記憶體執行：

```csharp
var bigPendingOrders = db.Orders
    .Where(order => !order.Shipped)
    .AsEnumerable()
    .Where(OrderRules.IsBig)
    .ToList();
```

實際 SQL：

```sql
SELECT "o"."Id", "o"."Amount", "o"."Number", "o"."Shipped"
FROM "Orders" AS "o"
WHERE NOT ("o"."Shipped")
```

`AsEnumerable()` 只改變編譯期型別，保留前面已組好的 SQL；`ToList()` 則先把查詢結果緩衝成清單。加入 `AsEnumerable()` 前先確認該邏輯 EF Core 真的翻不出來；能翻的篩選與投影留在 `IQueryable`。

### 常見效能問題

以下片段位於可取得 `DbContext db` 與 `CancellationToken cancellationToken` 的 `async` 方法中。

```csharp
// 不好：只要訂單編號，卻先載入完整 entity。
var numbersFromEntities = (await db.Orders.ToListAsync(cancellationToken))
    .Select(order => order.Number)
    .ToList();

// 較好：只投影需要的欄位。
var numbersFromQuery = await db.Orders
    .Select(order => order.Number)
    .ToListAsync(cancellationToken);
```

其他常見問題：

- 在 loop 裡對每個 parent 另外查 child，產生 N+1 queries。
- 過早 `ToList()`，讓後續篩選／排序在應用程式記憶體執行。
- 在無法翻譯的地方加入自訂方法，導致例外或意外的 client evaluation。
- 沒有分頁，對大型資料表直接 `ToListAsync()`。
- 把 `IQueryable<T>` 從 repository 洩漏到太多層，讓上層任意組查詢，造成 transaction、追蹤與效能責任不清楚。

## 5. 常見誤解

- `IQueryable<T>` 不代表「一定是資料庫」；它只是 provider 的查詢抽象，記憶體集合也能透過 `AsQueryable()` 建立它。
- 寫了 `Where` 不代表已查資料庫；`ToListAsync`、`FirstAsync`、`AnyAsync`、`foreach` 與 `await foreach` 等列舉結果的操作才會觸發查詢。
- `ToListAsync` 不在 `IEnumerable<T>` 上；EF Core 提供 `IQueryable<T>` 的擴充方法，.NET 10 也提供 `IAsyncEnumerable<T>` 的 `System.Linq.AsyncEnumerable.ToListAsync`。
- `IEnumerable<T>` 可能是延遲 iterator，每次列舉都重新工作；`AsEnumerable()` 只切換後續運算子的靜態型別，不會自動縮小資料。

## 6. 面試怎麼回答

> `IEnumerable<T>` 代表在應用程式記憶體中列舉，LINQ to Objects 會直接執行委派。`IQueryable<T>` 讓 provider 取得 expression tree，EF Core 可以把查詢翻成 SQL。兩者都能延遲執行，直到 `ToListAsync`、`FirstAsync`、`AnyAsync` 或列舉結果時才真正查詢。效能上我會把篩選、投影、分頁盡量留在 `IQueryable`，避免過早 `ToList`、N+1 和把不必要的欄位載入記憶體。

## 7. 小練習

1. 判斷 `db.Orders.Where(...).ToListAsync()` 中哪一行觸發資料庫查詢。
2. 把「先查所有訂單再篩選」改成可由 SQL 翻譯的查詢。
3. 解釋 `AsEnumerable()` 為什麼是重要的執行位置切換點。
