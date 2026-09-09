---
title: 04 LINQ
tags: [csharp, linq, java-stream, collections]
---

# 04 LINQ：從 Java Stream API 轉過來

## 學習目標

- 把 Java Stream 的 filter / map / collect 對應到 LINQ。
- 理解 extension method、deferred execution 與 terminal operation。
- 讀懂 Web API service 中常見的資料篩選與 DTO projection。

## 1. 一句話理解

LINQ 是把查詢與轉換操作整合進 C# 的一組語法與 API；你可以用幾乎相同的 pipeline 思維處理 in-memory collection 或由 provider 翻譯的資料來源。

## 2. Java 對照

Java：

```java
var result = users.stream()
    .filter(User::isActive)
    .map(u -> new UserDto(u.id(), u.name()))
    .toList();
```

C#：

```csharp
var result = users
    .Where(x => x.IsActive)
    .Select(x => new UserDto(x.Id, x.Name))
    .ToList();
```

核心對照：

| Java Stream | C# LINQ |
| --- | --- |
| `filter` | `Where` |
| `map` | `Select` |
| `flatMap` | `SelectMany` |
| `sorted` | `OrderBy` / `ThenBy` |
| `collect(toList())` | `ToList()` |
| `anyMatch` | `Any` |
| `allMatch` | `All` |
| `findFirst` | `First` / `FirstOrDefault` |
| `findOne` 自行限制 | `Single` / `SingleOrDefault` |
| `Collectors.groupingBy` | `GroupBy` / `ToLookup` |

## 3. C# 語法

```csharp
var activeNames = users
    .Where(x => x.IsActive)
    .Select(x => x.Name)
    .OrderBy(name => name)
    .ToList();

var orderedUsers = users
    .OrderBy(x => x.Department)
    .ThenBy(x => x.Name)
    .ToList();
```

### 常用 operators

```csharp
var hasInactive = users.Any(x => !x.IsActive);
var allNamed = users.All(x => !string.IsNullOrWhiteSpace(x.Name));

var first = users.First();                 // 沒有元素會 throw
var maybeFirst = users.FirstOrDefault();   // 沒有元素回傳 default（reference type 通常是 null）

var exactlyOne = users.Single(x => x.Id == id);             // 0 或 >1 都 throw
var maybeOne = users.SingleOrDefault(x => x.Id == id);      // >1 throw；0 回 default

int count = users.Count();
int activeCount = users.Count(x => x.IsActive);
decimal total = orders.Sum(x => x.Amount);

var byDepartment = users.GroupBy(x => x.Department);
var nameById = users.ToDictionary(x => x.Id, x => x.Name);
```

`First` 和 `Single` 的差別不在哪個比較安全，在你對資料的假設。同一個 email 註冊了兩個帳號時：

```csharp
var accounts = new List<Account>
{
    new(1, "ada@example.com"),
    new(2, "ada@example.com"),
};

accounts.First(a => a.Email == "ada@example.com");   // Id=1，重複這件事被吃掉
accounts.Single(a => a.Email == "ada@example.com");  // InvalidOperationException:
                                                     // Sequence contains more than one matching element
```

email 唯一是 domain 規則。寫 `Single` 等於把規則放進 code，規則被破壞時會停在這一行；寫 `First` 則是後面每一段都拿到「其中一筆」，而且拿到哪一筆看順序。

### `SelectMany`

```csharp
var allRoles = users
    .SelectMany(x => x.Roles)
    .Distinct()
    .ToList();
```

它把每個 user 的 nested collection 展平成一條序列，概念上對應 Java `flatMap`。

### LINQ 是 extension method

```csharp
using System.Linq;

IEnumerable<string> names = users.Select(x => x.Name);
```

`Select` 並不是 `IEnumerable<T>` 的 instance method，而是 extension method；compiler 會把 `users.Select(...)` 解析成 `Enumerable.Select(users, ...)`。若來源是 `IQueryable<T>`，則會選 `Queryable.Select`，這是 [[05-IEnumerable與IQueryable]] 的關鍵。

## 4. 實務範例：service 將 entity projection 成 DTO

```csharp
public sealed record UserDto(Guid Id, string Name, DateTimeOffset CreatedAt);

public static IReadOnlyList<UserDto> ToActiveUserDtos(
    IEnumerable<User> users)
{
    return users
        .Where(x => x.IsActive)
        .OrderBy(x => x.Name)
        .Select(x => new UserDto(
            x.Id,
            x.Name,
            x.CreatedAt))
        .ToList(); // 在這裡 materialize 成 snapshot
}
```

為什麼先 `Where` 再 `Select`？通常是先縮小資料量、再做 projection；若來源是 EF Core，這也有機會讓 SQL 只選需要的 rows / columns。實際 SQL 能否最佳化仍要看 provider 與 query plan。

### deferred execution vs immediate execution

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
```

`pending` 不是結果，是一份還沒跑的查詢。它在 `Count()` 那一刻才去看 `orders`，看到的是當下的內容，所以後來才加的 A003 也在裡面。

`checkCount` 是 6：三筆訂單、列舉兩次。這裡的 predicate 只是讀一個 bool 所以看不出差別；換成要查資料庫或呼叫 API 的判斷，同一段 code 就是兩倍成本。要固定結果、也只算一次，就 `ToList()`。

`Where`、`Select`、`OrderBy` 等通常建立 lazy pipeline；`ToList`、`ToArray`、`ToDictionary`、`Count`、`First`、`Single`、`Any` 等會觸發列舉或查詢。

## 5. 常見誤解

- LINQ query 沒有因為寫在變數裡就執行；要看是否已列舉或 terminal operator。
- `ToList()` 不是「只轉型」；它會列舉、配置 list、複製結果，並可能觸發 SQL。
- `Select` 是 projection；不要把它和 `Where` 混用。
- `Count()` 可能列舉整個序列；若已經是 collection，`Count` property 通常更直接。
- `Single` 不是「比 `First` 更安全的 First」；它明確驗證最多只能有一筆。

## 6. 面試怎麼回答

> LINQ 是 C# 的 query operators，`Where` 篩選、`Select` projection、`SelectMany` flatten、`OrderBy` 排序、`GroupBy` 分組。多數 operators 是 deferred execution，真正列舉或呼叫 `ToList`、`First`、`Count` 等才執行。對 `IEnumerable<T>` 是在記憶體跑；對 `IQueryable<T>` 則可能被 provider 轉成 SQL，所以不能只看 C# 表面就假設執行位置。

## 7. 小練習

1. 把 Java `filter → map → toList` 改成 C# LINQ。
2. 何時用 `SingleOrDefault` 而不是 `FirstOrDefault`？
3. 寫一段 LINQ，把每個 user 的 roles flatten 後去重並排序。
