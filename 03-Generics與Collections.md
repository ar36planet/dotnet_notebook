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

Generic 讓 collection 保持 compile-time type safety；collection interface 則是在 API 邊界上只暴露呼叫者真正需要的能力。

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

典型 signature：

```csharp
public interface IUserReader
{
    IEnumerable<User> StreamUsers();
    IReadOnlyCollection<User> GetUsers();
    IReadOnlyList<User> GetUsersInOrder();
}

public interface IUserStore
{
    ICollection<User> MutableUsers { get; }
    List<User> GetInternalList(); // 通常不應把實作型別暴露到 public boundary
}
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

```csharp
public static T? FindById<T>(
    IEnumerable<T> items,
    Func<T, bool> predicate)
    => items.FirstOrDefault(predicate);
```

`T` 是 type parameter；`Func<T, bool>` 是 delegate，代表一個接收 `T`、回傳 `bool` 的 function。C# 也支援 generic constraints：

```csharp
public static T Create<T>() where T : new()
    => new T();
```

## 4. 實務範例：service boundary 的回傳型別

```csharp
public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveUsersAsync(
        CancellationToken cancellationToken);
}

public sealed class UserRepository : IUserRepository
{
    public async Task<IReadOnlyList<User>> GetActiveUsersAsync(
        CancellationToken cancellationToken)
    {
        // 實務上可能是 EF Core ToListAsync(cancellationToken)。
        await Task.Yield();
        return _users.Where(x => x.IsActive).ToList();
    }

    private readonly List<User> _users = [];
}
```

為什麼不直接回傳 `List<User>`？因為 repository 只需要承諾「這是一組可讀、有順序的結果」，不需要讓 caller 依賴 list-specific API；未來可以改成 array、EF materialized result 或其他實作，而不必改變 contract。

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
