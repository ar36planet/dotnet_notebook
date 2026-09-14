---
title: 03 Generics 與 Collections
tags: [csharp, generics, collections, api-design]
---

# 03 Generics 與 Collections

## 學習目標

- 看懂 `List<Order>` 的 `<Order>`，知道泛型解決什麼問題。
- 用清單存多筆訂單，用字典依訂單編號查資料。
- 分清楚集合本身與介面提供的操作，選出合適的回傳型別。

## 1. 一句話理解

集合用來處理多筆資料；泛型指定裡面的資料型別，例如 `List<Order>` 就是一份裝訂單的清單。

## 2. Java 對照

這張表只供查名稱：

| 用途 | C# | Java 大致對應 |
| --- | --- | --- |
| 存一份可增刪的清單 | `List<T>` | `ArrayList<T>` |
| 用鍵查值，例如用訂單編號查訂單 | `Dictionary<TKey, TValue>` | `HashMap<K, V>` |
| 允許逐筆讀取的介面 | `IEnumerable<T>` | `Iterable<T>` |

C# 的 `List<T>` 是可以用 `new` 建立的類別。Java 的 `List<T>` 是介面，兩者雖然同名，角色不同。

## 3. C# 語法

### 從一筆訂單到一份清單

只用 `order1`、`order2` 存訂單，每多一筆就得新增變數，也無法用同一段迴圈處理全部訂單。改用 `List<Order>`，新增資料只需要呼叫 `Add`。

以下範例用 .NET 10 主控台專案執行；把整段放進 `Program.cs`。後面延續此範例的程式碼，放在最後的 `record` 宣告之前。

```csharp
List<Order> orders =
[
    new Order(1001, 800m),
    new Order(1002, 1200m)
];

orders.Add(new Order(1003, 500m));
Console.WriteLine($"筆數：{orders.Count}");
Console.WriteLine($"第一筆：{orders[0].Id}");

foreach (Order order in orders)
    Console.WriteLine($"訂單 {order.Id}：{order.Amount} 元");

public sealed record Order(int Id, decimal Amount);
```

實際輸出：

```text
筆數：3
第一筆：1001
訂單 1001：800 元
訂單 1002：1200 元
訂單 1003：500 元
```

`Order` 表示一筆訂單，有編號 `Id` 和金額 `Amount`；`800m` 的 `m` 表示 `decimal`。`orders` 則存多筆 `Order`。

C# 12 起，collection expression 可以把初始化寫成 `List<Order> orders = [new(1001, 800m), new(1002, 1200m)];`。左側必須提供目標型別，不能寫成沒有目標型別的 `var orders = [...]`。

- `Add(...)`：把一筆訂單加到清單最後。
- `Count`：目前有幾筆，這是屬性，不加括號。
- `[0]`：取第一筆。索引從 0 開始，三筆資料的索引是 0、1、2。
- `foreach`：依序取出每筆訂單，放進變數 `order`，執行迴圈內的程式。

### `<T>` 指定裡面裝什麼

金額如果混入文字，後面加總就得處理型別錯誤。`List<decimal>` 把元素限定成金額使用的 `decimal`，加錯型別會在編譯時被擋下。

`List<T>` 的 `T` 是型別參數，使用時換成實際型別：`List<Order>` 裝訂單，`List<decimal>` 裝金額。這就是泛型：同一份清單功能，可以用在不同型別上，不必各寫一套。

### `Dictionary`：用編號查訂單

清單的 `orders[0]` 是依位置取資料。要依訂單編號查詢，可以建立字典：

```csharp
var byId = new Dictionary<int, Order>();
foreach (Order order in orders)
    byId.Add(order.Id, order);

if (byId.TryGetValue(1002, out Order? found))
    Console.WriteLine($"查到：{found.Id}，{found.Amount} 元");
```

實際輸出：

```text
查到：1002，1200 元
```

`Dictionary<int, Order>` 有兩個型別參數：鍵是 `int` 訂單編號，值是 `Order` 訂單。`TryGetValue` 找到時回傳 `true`，並把訂單放進 `found`；找不到時，這個 `if` 的內容就不執行。同一個字典的鍵不能重複。

### 同一份清單，可以透過不同介面使用

如果取得訂單的方法回傳 `List<Order>`，呼叫端就能呼叫 `Clear()`，把拿到的清單清空。若只需要顯示結果，可以把回傳型別寫成 `IReadOnlyList<Order>`，這個介面只有讀取操作。

```csharp
IReadOnlyList<Order> result = orders;
orders.Add(new Order(1004, 300m));
Console.WriteLine($"唯讀介面看到的筆數：{result.Count}");
```

實際輸出：

```text
唯讀介面看到的筆數：4
```

右側的 `orders` 仍是原來的 `List<Order>`；左側的型別決定透過 `result` 可以用哪些操作。`result` 能讀 `Count`、用 `[0]` 取資料，卻沒有 `Add` 或 `Clear`。這次指派沒有複製清單，所以透過 `orders` 新增的訂單，`result` 也看得到。

介面只限制透過該靜態型別可呼叫的操作；`result` 實際上仍是同一個 `List<Order>`，呼叫端可以用 `((List<Order>)result).Clear()` 轉型後修改它。若要連這種轉型也擋住，回傳 `orders.AsReadOnly()` 產生的 `ReadOnlyCollection<Order>` 包裝：

```csharp
IReadOnlyList<Order> protectedResult = orders.AsReadOnly();
try
{
    ((List<Order>)protectedResult).Clear();
}
catch (InvalidCastException exception)
{
    Console.WriteLine($"{exception.GetType().FullName}: {exception.Message}");
}
```

實際輸出：

```text
System.InvalidCastException: Unable to cast object of type 'System.Collections.ObjectModel.ReadOnlyCollection`1[Order]' to type 'System.Collections.Generic.List`1[Order]'.
```

| 型別 | 可以使用的主要操作 |
| --- | --- |
| `IEnumerable<Order>` | 用 `foreach` 逐筆讀取 |
| `IReadOnlyCollection<Order>` | 逐筆讀取、用 `Count` 讀筆數 |
| `IReadOnlyList<Order>` | 逐筆讀取、`Count`、用索引取資料 |
| `ICollection<Order>` | 逐筆讀取、`Count`，並提供 `Add`、`Remove` 等方法；唯讀實作可能拒絕修改 |
| `List<Order>` | 可增刪的清單，也能用索引取資料、排序 |

`IEnumerable<Order>` 只承諾能逐筆讀取。已經存好資料的 `List<Order>` 也實作這個介面，所以看到它，不能直接判定資料還沒取回來。

### 自訂泛型：分頁結果

自己的資料型別也能用 `T`。分頁結果需要「本頁資料」與「符合條件的總筆數」，可寫成：

```csharp
var page = new PagedResult<Order>(orders, 4);
Console.WriteLine($"本頁 {page.Items.Count} 筆，共 {page.TotalCount} 筆");
```

延續上例，此時 `orders` 有四筆，輸出：

```text
本頁 4 筆，共 4 筆
```

在檔案最下方加上型別宣告：

```csharp
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
```

`PagedResult<Order>` 裡的 `Items` 就是 `IReadOnlyList<Order>`。呼叫端可以讀取本頁筆數與各筆訂單。

## 4. 實務範例：回傳最近的訂單

訂單頁需要顯示筆數與第一筆訂單，選 `IReadOnlyList<Order>` 就足夠。延續前面的 `Order` 宣告：

```csharp
IReadOnlyList<Order> recent = GetRecentOrders();
Console.WriteLine($"最近 {recent.Count} 筆，第一筆是 {recent[0].Id}");

static IReadOnlyList<Order> GetRecentOrders()
{
    List<Order> orders =
    [
        new Order(1003, 500m),
        new Order(1002, 1200m)
    ];
    return orders;
}
```

實際輸出：

```text
最近 2 筆，第一筆是 1003
```

方法內用 `List<Order>` 建立資料，方法外透過 `IReadOnlyList<Order>` 讀取。這裡資料已經備妥，是因為方法內建立了清單。

這個範例固定有兩筆。實際查詢若可能回傳空清單，使用 `[0]` 前要先確認 `Count > 0`。

### 查詢條件不會自動存成結果

下面是獨立範例。`Where` 是 LINQ 的篩選方法，`amount => amount >= 1000m` 表示保留金額至少 1000 元的項目；完整語法留到 [[04-LINQ]]。

```csharp
List<decimal> amounts = [800m, 1200m];
IEnumerable<decimal> largeAmounts = amounts.Where(amount => amount >= 1000m);

amounts.Add(1500m);
Console.WriteLine(string.Join(", ", largeAmounts));

List<decimal> snapshot = largeAmounts.ToList();
amounts.Add(2000m);
Console.WriteLine(string.Join(", ", snapshot));
Console.WriteLine(string.Join(", ", largeAmounts));
```

實際輸出：

```text
1200, 1500
1200, 1500
1200, 1500, 2000
```

第一行包含後來新增的 1500：`Where` 在這裡先保存篩選條件，直到 `string.Join` 逐筆讀取時才篩選。`ToList()` 會當場讀取結果，存成另一份清單，因此 `snapshot` 沒有後來新增的 2000；再次讀取 `largeAmounts` 則會重新篩選。

資料庫查詢和執行時機放在 [[05-IEnumerable與IQueryable]]。

## 5. 常見誤解

- **唯讀介面等於資料永遠不變。** 它限制透過該介面能做的操作；若底層仍是 `List<Order>`，呼叫端甚至可以轉型回去修改。真的要防止清單本身被修改，使用 `AsReadOnly()` 包裝；這仍不保證每筆物件的屬性不能改。
- **`ToList()` 會複製所有物件。** 它建立新的清單；元素若是參考型別，新舊清單仍可指向同一個物件。
- **用了介面就不能用 `List<T>`。** 方法內仍可用清單；介面決定呼叫端可用的操作。需要讓呼叫端直接增刪、排序時，回傳 `List<T>` 也合理。

## 6. 面試怎麼回答

> 泛型讓同一份程式碼處理不同型別，例如 `List<Order>` 只能加入符合型別的訂單。集合型別則根據用途選：依序存多筆資料用 `List<T>`，用編號查資料用 `Dictionary<TKey, TValue>`。回傳資料時，我會看呼叫端需要哪些操作；只逐筆讀取可用 `IEnumerable<T>`，還需要筆數和索引可用 `IReadOnlyList<T>`。唯讀介面不保證底層資料不變，也不決定查詢何時執行。

## 7. 小練習

1. 在第一個範例新增訂單 1004、金額 300 元。執行前先預測 `Count` 與 `orders[3].Id`，再跑程式核對。
2. 把字典範例查詢的編號改成不存在的 9999，補上 `else` 顯示「查無訂單」。
3. 一個方法只供逐筆列印訂單，另一個還要顯示筆數與第一筆。各選哪個回傳介面？
4. 把最後範例的 `ToList()` 移到新增 1500 之前，預測三行輸出。
