---
title: 03 Generics 與 Collections
tags: [csharp, generics, collections, api-design]
---

# 03 Generics 與 Collections

## 學習目標

- 把 Java generic type argument 的經驗移到 C#。
- 理解 `IEnumerable<T>`、`IReadOnlyList<T>` 等 abstraction 暴露的是什麼能力。
- 能為 service / repository 寫出不過度暴露實作的 method signature。

## 1. 一句話理解

回傳型別是一份承諾。signature 上寫 `IReadOnlyList<T>` 還是 `List<T>`，決定的是呼叫者能對這份資料做什麼、以及你之後還能不能換掉實作。generic 則讓這份承諾連「裡面裝什麼」都由 compiler 幫你盯。

## 2. Java 對照

| C# | Java 大致對應 |
| --- | --- |
| `List<T>` | `List<T>` |
| `Dictionary<TKey,TValue>` | `Map<K,V>` / 常見實作 `HashMap<K,V>` |
| `IEnumerable<T>` | `Iterable<T>` 加上可組合的 LINQ extension；更接近「可列舉序列」 |
| `IReadOnlyCollection<T>` | 沒有完全一樣的標準 interface；可理解成只讀、知道 `Count` 的 collection view |
| `IReadOnlyList<T>` | `List` 的 read-only indexable view，概念上接近不可變的 `List` API 子集 |
| `ICollection<T>` | 可讀、可計數、可加入／移除的 collection contract |

`IEnumerable<T>` 不等於 Java `Stream<T>`：Java Stream 是一次性的 pipeline abstraction；`IEnumerable<T>` 通常可以重新列舉，但若來源是 iterator、DB query 或有 side effect，重複列舉仍可能有成本或不同結果。

## 3. C# 語法

先看一個會出事的寫法。

```csharp
// repository
public IEnumerable<User> GetActiveUsers()
    => _context.Users.Where(x => x.IsActive); // 沒有 ToList

// controller
var users = _repository.GetActiveUsers();
var total = users.Count();      // 查一次資料庫
foreach (var user in users) { } // 再查一次
```

回傳 `IEnumerable<T>` 等於說「我給你一份還沒算的東西，你什麼時候列舉，我什麼時候去拿」。上面這段查了兩次資料庫。更麻煩的是，`DbContext` 如果在 controller 用到它之前就被 dispose，`ObjectDisposedException` 會在列舉的那一刻才炸，而 stack trace 指的是 controller，不是那個忘了寫 `ToList` 的 repository。這種東西要找很久。

先查完再回傳，型別也一起換掉：

```csharp
public async Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken ct)
    => await _context.Users
        .Where(x => x.IsActive)
        .ToListAsync(ct);
```

`IReadOnlyList<User>` 在 signature 上講了兩件事：資料已經在手上，還有你別改它。

基本的 collection 長這樣：

```csharp
public sealed record User(Guid Id, string Name);

var users = new List<User>
{
    new(Guid.NewGuid(), "Ada"),
    new(Guid.NewGuid(), "Grace")
};

var byId = new Dictionary<Guid, User>
{
    [users[0].Id] = users[0],
    [users[1].Id] = users[1]
};
```

### interface collection vs concrete collection

| 回傳型別 | 暴露的能力 | 適合情境 |
| --- | --- | --- |
| `IEnumerable<User>` | 只能往前列舉 | 呼叫者只需要逐筆讀；來源可能 lazy；不承諾 count 或 index |
| `IReadOnlyCollection<User>` | 列舉 + `Count` | 回傳一批結果，呼叫者需要知道數量但不需要 index |
| `IReadOnlyList<User>` | 列舉 + `Count` + index | 順序與 index 是 API 語意的一部分 |
| `ICollection<User>` | 讀取 + `Add` / `Remove` 等 mutation | 呼叫者確實被允許修改 collection |
| `List<User>` | 上述能力加上 concrete implementation 的細節 | local code、需要 `List` API，或明確承諾這個 concrete type |

重要：`IReadOnlyList<T>` 是「透過這個 reference 不提供 mutation API」，不保證底層 collection 永遠不變。若要真正隔離，可回傳 copy 或 immutable collection：

```csharp
public IReadOnlyList<User> GetUsers()
    => _users.ToList(); // 呼叫者拿到 snapshot；不是同一個可變 list reference
```

### 泛型 method

分頁大概是最常自己寫 generic 的地方：

```csharp
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public async Task<PagedResult<User>> GetUsersAsync(
    int page,
    int pageSize,
    CancellationToken ct)
{
    var query = _context.Users.Where(x => x.IsActive);

    var total = await query.CountAsync(ct);
    var items = await query
        .OrderBy(x => x.CreatedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(ct);

    return new PagedResult<User>(items, total, page, pageSize);
}
```

`T` 是 type parameter，`PagedResult<T>` 寫一次，`User`、`Order` 都能用；而且 compiler 會擋掉「把 `PagedResult<User>` 當成 `PagedResult<Order>` 傳」這種事。

constraint 是規定 `T` 至少要長什麼樣。掛上 `where T : Entity`（02 那個有 `Id` 的 abstract class），compiler 才讓你在裡面碰 `x.Id`：

```csharp
public static T? FindById<T>(IEnumerable<T> items, Guid id) where T : Entity
    => items.FirstOrDefault(x => x.Id == id);
```

## 4. 實務範例：service boundary 的回傳型別

```csharp
public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken cancellationToken);
}

public sealed class UserRepository(AppDbContext context) : IUserRepository
{
    public async Task<IReadOnlyList<User>> GetActiveUsersAsync(
        CancellationToken cancellationToken)
        => await context.Users
            .Where(x => x.IsActive)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
}
```

為什麼不直接回傳 `List<User>`？repository 要承諾的只有「這是一組可讀、有順序的結果」。回傳 `List<User>`，caller 就能對它 `Add`、`Clear`、`Sort`——那份 list 是剛從資料庫撈出來的副本，改了不會進資料庫，但後面的程式很容易以為改到了。另外，哪天你想改成回傳 array 或別的結果型別，contract 也得跟著動。

選擇時可以問三個問題：

1. 呼叫者是否需要 index？需要就考慮 `IReadOnlyList<T>`。
2. 呼叫者是否需要 `Count`？需要就考慮 `IReadOnlyCollection<T>` 或 list。
3. 呼叫者是否被允許修改集合？若不是，避免回傳 `ICollection<T>` / `List<T>`。

## 5. 常見誤解

- 回傳 interface 不會自動複製資料；它只是縮小 static API。
- `IEnumerable<T>` 不保證已經 materialized；可能每次列舉都重新查詢或重新計算。
- `IReadOnlyCollection<T>` 不等於 immutable collection。
- `List<T>` 不是「錯」，但 public boundary 直接回傳它會讓 caller 依賴具體實作。
- collection element 的 nullable 也要表達清楚：`IReadOnlyList<User?>` 與 `IReadOnlyList<User>` 不同。

## 6. 面試怎麼回答

> 我會根據呼叫者需要的能力選 collection abstraction。只需要逐筆讀就回傳 `IEnumerable<T>`；需要 count 用 `IReadOnlyCollection<T>`；需要順序與 index 用 `IReadOnlyList<T>`。只有當 caller 真的要修改 collection 或 API 明確承諾 concrete behavior 時才用 `ICollection<T>` 或 `List<T>`。這樣可以降低 coupling，但要注意 interface read-only 不代表底層物件一定 immutable。

## 7. 小練習

1. 為「取得使用者最近 20 筆登入紀錄」選回傳型別，說明是否需要 index。
2. 把 `List<User> GetUsers()` 改成較合適的 public signature。
3. 解釋為什麼 `IEnumerable<User>` 可能在第二次 `foreach` 時又做一次昂貴工作。
