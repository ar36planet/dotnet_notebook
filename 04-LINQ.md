---
title: 04 LINQ
tags: [csharp, linq, collections]
---

# 04 LINQ：把一串資料處理成結果

## 學習目標

- 用 LINQ 篩選、排序、分組，並把實體轉成 DTO。
- 理解 extension method、延遲執行與立即執行。
- 讀懂 Web API service 中常見的資料篩選與 DTO 投影。

## 1. 一句話理解

LINQ 是把查詢與轉換操作整合進 C# 的一組語法與 API；同一條查詢鏈可以處理記憶體中的集合，也可以交給資料來源 provider 翻譯。

## 2. Java 對照

| Java Stream | C# LINQ | 備註 |
| --- | --- | --- |
| `filter` | `Where` | 篩選元素 |
| `map` | `Select` | 投影成另一種值 |
| `flatMap` | `SelectMany` | 展平巢狀集合 |
| `sorted` | `OrderBy`／`ThenBy` | 排序 |
| `collect(groupingBy(...))` | `GroupBy` | 分組；C# 結果是 `IGrouping<TKey, TElement>` |
| `reduce` | `Aggregate` | 累積成單一結果 |
| `findFirst` | `First`／`FirstOrDefault` | Java 通常回 `Optional`，C# 的 `OrDefault` 回傳 `default(T)` 或指定的預設值 |
| `collect(toMap(...))` | `ToDictionary` | key 重複時，Java 丟 `IllegalStateException`，C# 丟 `ArgumentException` |

Java Stream 在生命週期中只能走訪一次；同一個 `Stream` 第二次執行 terminal operation 會丟 `IllegalStateException`。`IEnumerable<T>` 的查詢變數則能被重複列舉，每次都重新執行查詢鏈，因此本章後面的 `pending.Count()` 可以呼叫兩次，但 predicate 也會跑兩次。

## 3. C# 語法

### 先看會出事的地方

帳號 email 應該唯一，卻寫成 `First` 時，重複資料不會立刻被發現：

```csharp
var accounts = new List<Account>
{
    new(1, "ada@example.com", ["billing"]),
    new(2, "ada@example.com", ["billing", "refund"]),
};

var account = accounts.First(a => a.Email == "ada@example.com");
Console.WriteLine(account.Id); // 1：重複資料被悄悄忽略

try
{
    accounts.Single(a => a.Email == "ada@example.com");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
}

public sealed record Account(int Id, string Email, IReadOnlyList<string> Roles);
```

實際輸出：

```text
1
Sequence contains more than one matching element
```

如果「最多只能有一筆」是資料規則，應寫成 `Single`。規則被破壞時，程式要在查詢這一行停下來，而不是讓後面的程式拿到不確定的帳號。

`First` 表示「至少一筆，拿第一筆」；`Single` 表示「剛好一筆」。

### 常用語法

以下片段接續前面的 `accounts`，並使用相同的 `orders` 資料。

```csharp
var accountEmails = accounts
    .Where(account => account.Email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase))
    .Select(account => account.Email)
    .OrderBy(email => email)
    .ToList();

var orderedOrders = orders
    .OrderBy(order => order.Amount)
    .ThenBy(order => order.Number)
    .ToList();
```

### 常用運算子

```csharp
var accounts = new List<Account>
{
    new(1, "ada@example.com", ["billing"]),
    new(2, "grace@example.com", ["billing", "refund"]),
};

var orders = new List<Order>
{
    new(1001, "A001", 1200m, Shipped: false),
    new(1002, "A002", 890m, Shipped: true),
    new(1003, "A003", 450m, Shipped: false),
};

var hasExampleAccount = accounts.Any(account => account.Email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase));
var allAccountsHaveEmail = accounts.All(account => account.Email.Contains('@'));

var firstAccount = accounts.First();                 // 沒有元素會 throw
var maybeFirstAccount = accounts.FirstOrDefault();   // reference type 的 default 一定是 null
var fallbackAccount = accounts.FirstOrDefault(account => account.Id == 999, new Account(999, "unknown@example.com", []));

var exactlyOne = accounts.Single(account => account.Email == "ada@example.com"); // 0 或 >1 都 throw
var maybeOne = accounts.SingleOrDefault(account => account.Id == 999);            // >1 throw；0 回 null

int count = accounts.Count();
int premiumOrderCount = orders.Count(order => order.Amount >= 1000m);
decimal total = orders.Sum(x => x.Amount);

var byShippedStatus = orders.GroupBy(order => order.Shipped);
var emailById = accounts.ToDictionary(account => account.Id, account => account.Email);

foreach (var group in byShippedStatus)
    Console.WriteLine($"Shipped={group.Key}: {group.Count()}");

public sealed record Account(int Id, string Email, IReadOnlyList<string> Roles);
public sealed record Order(int Id, string Number, decimal Amount, bool Shipped);
```

`GroupBy` 回傳 `IGrouping<TKey, TElement>` 的集合；它是延遲、但非串流的運算，第一次列舉時要先讀完來源才能建立分組。`ToDictionary` 則要求每個 key 唯一。

實際輸出：

```text
Shipped=False: 2
Shipped=True: 1
```

以下兩個片段接續上面的 `accounts` 與 `orders`。

```csharp
var ordersByStatus = from order in orders
                     where order.Amount >= 1000m
                     orderby order.Amount descending
                     select order.Number;

var sameQuery = orders
    .Where(order => order.Amount >= 1000m)
    .OrderByDescending(order => order.Amount)
    .Select(order => order.Number);
```

query syntax 會由編譯器轉成同一組 extension method 呼叫；上面兩個查詢的語意相同。需要分組、連接或累加時，可直接使用 `GroupBy`、`Join`、`Aggregate`：

```csharp
var amountByStatus = orders
    .GroupBy(order => order.Shipped)
    .ToDictionary(group => group.Key, group => group.Sum(order => order.Amount));

var orderWithAccount = orders.Join(
    accounts,
    order => order.Id,
    account => account.Id,
    (order, account) => $"{account.Email}: {order.Number}");

decimal totalByLoop = orders.Aggregate(0m, (total, order) => total + order.Amount);
```

### `SelectMany`

以下片段接續前例的 `accounts`。

```csharp
var allRoles = accounts
    .SelectMany(account => account.Roles)
    .Distinct()
    .ToList();
```

它把每個 account 的巢狀集合展平成一條序列。

### LINQ 是 extension method

以下片段接續前例的 `accounts`。

```csharp
using System.Linq;

IEnumerable<string> accountEmails = accounts.Select(account => account.Email);
```

`Select` 並不是 `IEnumerable<T>` 的 instance method，而是 extension method；編譯器會把 `accounts.Select(...)` 解析成 `Enumerable.Select(accounts, ...)`。若來源是 `IQueryable<T>`，則會選 `Queryable.Select`，這是 [[05-IEnumerable與IQueryable]] 的關鍵。

### .NET 6／9 的 LINQ API

| 最低版本 | API | 用途 |
| --- | --- | --- |
| .NET 6 | `Chunk` | 把序列切成固定大小的區塊 |
| .NET 6 | `DistinctBy`、`MinBy`、`MaxBy` | 依指定 key 去重或找最小／最大元素 |
| .NET 6 | `TryGetNonEnumeratedCount` | 嘗試不列舉來源就取得數量 |
| .NET 6 | `FirstOrDefault`／`SingleOrDefault` 的預設值多載 | 沒有元素時使用指定值 |
| .NET 9 | `CountBy` | 依 key 計數，不必先建立 `GroupBy` 結果 |
| .NET 9 | `AggregateBy` | 依 key 累加狀態 |
| .NET 9 | `Index` | 以 `(Index, Item)` 形式取得列舉索引 |

## 4. 實務範例：service 將 entity projection 成 DTO

這個完整片段把帳號 entity 轉成 API DTO：

```csharp
var accounts = new List<Account>
{
    new(1, "ada@example.com", ["billing"]),
    new(2, "grace@example.com", ["billing", "refund"]),
};

var result = ToAccountDtos(accounts);
Console.WriteLine(string.Join(", ", result.Select(account => account.Email)));

static IReadOnlyList<AccountDto> ToAccountDtos(
    IEnumerable<Account> accounts)
{
    return accounts
        .Where(account => account.Email.EndsWith("@example.com"))
        .OrderBy(account => account.Email)
        .Select(account => new AccountDto(account.Id, account.Email))
        .ToList(); // 在這裡具體化成一份快照
}

public sealed record Account(int Id, string Email, IReadOnlyList<string> Roles);
public sealed record AccountDto(int Id, string Email);
```

實際輸出：

```text
ada@example.com, grace@example.com
```

記憶體集合中，先 `Where` 可以省掉被丟棄元素的 projection。EF Core 中，`Select` 決定 `SELECT` 的欄位、`Where` 決定 `WHERE`；兩者順序對調通常產生相同 SQL，實際 SQL 見 [[28-EF-Core-Query與Methods]]。

### 延遲執行 vs 立即執行

出貨清單先篩出還沒出貨的訂單，中間又進來一筆新訂單：

```csharp
var orders = new List<Order>
{
    new(1, "A001", 1200m, Shipped: false),
    new(2, "A002", 890m, Shipped: true),
};

int checkCount = 0;
var pending = orders.Where(o => { checkCount++; return !o.Shipped; });
// 這行跑完 checkCount 還是 0，predicate 一次都沒跑

orders.Add(new Order(3, "A003", 450m, Shipped: false));

Console.WriteLine(pending.Count()); // 2，A003 也被算進去
Console.WriteLine(pending.Count()); // 2
Console.WriteLine(checkCount);      // 6

public sealed record Order(int Id, string Number, decimal Amount, bool Shipped);
```

`pending` 不是結果，是一份還沒跑的查詢。它在 `Count()` 那一刻才去看 `orders`，看到的是當下的內容，所以後來才加的 A003 也在裡面。

`checkCount` 是 6：三筆訂單、列舉兩次。這裡的 predicate 只是讀一個 bool；換成要查資料庫或呼叫 API 的判斷，同一段程式碼就會重複付出成本。要固定結果、也只算一次，就 `ToList()`。

`Where`、`Select` 逐筆流過來源；`OrderBy`、`GroupBy`、`ThenBy`、`Reverse` 也是延遲執行，但第一次列舉時會先把整個來源讀進記憶體排序或分組。`ToList`、`ToArray`、`ToDictionary`、`Count`、`First`、`Single`、`Any` 等會觸發列舉或查詢。

## 5. 常見誤解

- LINQ 查詢沒有因為寫在變數裡就執行；要看是否已列舉或呼叫立即執行的運算子。
- `ToList()` 不是「只轉型」；它會列舉、配置清單、複製結果，並可能觸發 SQL。
- `Select` 只做 projection，不會過濾元素；要排除元素使用 `Where`。
- 對 `List<T>` 這類 collection，`Count()` 直接讀 `Count` 屬性；對 `Where` 後的延遲查詢，`Count()` 會把整條查詢鏈跑一遍。`.NET 6` 起可用 `TryGetNonEnumeratedCount` 嘗試在不列舉的情況下取得數量。
- `Single` 多做一件事：驗證符合條件的資料只有一筆。

## 6. 面試怎麼回答

> LINQ 是 C# 的查詢運算子：`Where` 篩選、`Select` 投影、`SelectMany` 展平、`OrderBy` 排序、`GroupBy` 分組。多數運算子採延遲執行，真正列舉或呼叫 `ToList`、`First`、`Count` 等才執行。對 `IEnumerable<T>` 是在記憶體跑；對 `IQueryable<T>` 則可能被 provider 轉成 SQL，所以不能只看 C# 表面就假設執行位置。

## 7. 小練習

1. 何時用 `SingleOrDefault` 而不是 `FirstOrDefault`？
2. 寫一段 LINQ，把每個 account 的 roles 展平後去重並排序。
3. 把 `ToList()` 移到 deferred query 的不同位置，預測每次列舉會看到哪些訂單。
