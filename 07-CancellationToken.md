---
title: 07 CancellationToken
tags: [aspnet-core, cancellation, async, http]
---

# 07 `CancellationToken`：讓 request 可以停止工作

## 學習目標

- 了解客戶端斷線與伺服器取消如何傳到應用程式碼。
- 將 token 一路傳到 EF Core、HttpClient、stream copy。
- 分清楚合作式取消與強制終止執行緒。

## 1. 一句話理解

`CancellationToken` 是「請在安全的檢查點停止」的通知；它不會強制終止執行緒，而是讓每一層合作地停止不再需要的工作。

## 2. Java 對照

| C# | Java | 差異 |
| --- | --- | --- |
| `IsCancellationRequested` | `Thread.interrupted()`／中斷旗標 | 都是檢查取消通知；.NET token 不綁定特定執行緒 |
| `ThrowIfCancellationRequested()` | `InterruptedException` | 都讓工作從安全檢查點離開，但例外由各 API 決定 |
| `CancellationTokenSource.Cancel()` | `Future.cancel(true)` | 都是請求停止，不保證工作立刻終止 |
| `Thread.Abort` | `Thread.stop` | 都不是現代 .NET／Java 的正規取消方式；.NET Core 起 `Thread.Abort` 會拋 `PlatformNotSupportedException` |

Java 的中斷狀態綁在 thread 上；.NET 的 `CancellationToken` 是可傳給任意方法的值型別。Java 21 的 `Future.cancel(true)` 也是請求執行緒中斷，不等於安全地回滾副作用。

## 3. C# 語法

### 先看會出事的地方

使用者關閉訂單查詢頁後，server 仍可能繼續查資料庫、呼叫外部 API，最後才發現 response 已經沒有接收者。只在 controller 宣告 token 還不夠，必須把同一個 token 傳進每一個支援取消的 I/O：

```csharp
public async Task<IReadOnlyList<Order>> GetOrdersAsync(
    Guid userId,
    CancellationToken cancellationToken)
{
    return await db.Orders
        .Where(order => order.UserId == userId)
        .ToListAsync(); // 忘了傳入 cancellationToken
}
```

這段程式宣告了 token，卻沒有傳給 `ToListAsync`；客戶端斷線時，這次資料庫查詢不會因 caller 取消而自動停止。

```csharp
public async Task<IReadOnlyList<Order>> GetOrdersAsync(
    Guid userId,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    return await db.Orders
        .Where(order => order.UserId == userId)
        .ToListAsync(cancellationToken);
}
```

`ThrowIfCancellationRequested()` 會拋出 `OperationCanceledException`。`Task.Delay`、`HttpClient` 與部分 EF Core provider 取消時常見的是它的子類 `TaskCanceledException`；catch `OperationCanceledException` 可以同時接住兩者。

實際訊息：

```text
System.OperationCanceledException: The operation was canceled.
```

`HttpClient.Timeout` 預設是 100 秒；逾時在 .NET 5+ 也會拋 `TaskCanceledException`，但內層包含 `TimeoutException`。因此不能把所有 `OperationCanceledException` 都當成客戶端斷線。

實際輸出（`HttpClient.Timeout = TimeSpan.FromSeconds(0.2)`）：

```text
Http timeout: System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 0.2 seconds elapsing. | inner=System.TimeoutException
Http (caller token): System.Threading.Tasks.TaskCanceledException: A task was canceled.
```

## 4. 實務範例：`RequestAborted` 一路傳遞

ASP.NET Core controller action 可以直接宣告 `CancellationToken`；框架會把 `HttpContext.RequestAborted` 綁進這個參數：

```csharp
[HttpGet]
public Task<IReadOnlyList<OrderDto>> GetOrders(
    CancellationToken cancellationToken)
    => _service.GetOrdersAsync(cancellationToken);
```

在較底層也可以使用：

```csharp
var token = HttpContext.RequestAborted;
```

典型鏈路：

```text
客戶端斷線／伺服器中止／request timeout middleware 逾時
        ↓
HttpContext.RequestAborted 被 signal
        ↓
controller / endpoint 的 CancellationToken
        ↓
service
        ↓
EF Core ToListAsync(token) / HttpClient GetAsync(..., token)
        ↓
provider 或 outbound HTTP 嘗試停止工作
```

EF Core 會把 token 傳給底層 database provider；provider 是否真的中止進行中的命令、多久才停止，要依 provider 文件確認，不能把它承諾成立即取消。

### HttpClient 與 cancellation

```csharp
public async Task<OrderSummary?> GetOrderAsync(
    Guid id,
    CancellationToken cancellationToken)
{
    using var response = await _httpClient.GetAsync(
        $"orders/{id}", cancellationToken);

    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return null;
    }

    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<OrderSummary>(
        cancellationToken);
}
```

### 自己撰寫的長工作

```csharp
public async Task ProcessAsync(
    IEnumerable<Order> orders,
    CancellationToken cancellationToken)
{
    foreach (var order in orders)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RecalculateOrderTotalAsync(order, cancellationToken);
    }
}
```

不要把 `OperationCanceledException` 當成一般 error log。若它是客戶端斷線，讓例外往上拋；ASP.NET Core 8+ 已掛 `UseExceptionHandler` 時，`RequestAborted` 已取消且 response 尚未開始，middleware 會記錄並回 `499 Client Closed Request`。若需要清理或補償，才在這一層 catch 後重新拋出。

## 5. 常見誤解

- 收到 token 不代表 method 一定會停；底層 API 必須支援它，或你要自行在 loop / checkpoint 檢查。
- 不要在 service 裡無視 caller token，另建一個永不取消的 token，除非是刻意設計的背景工作。
- `HttpContext.RequestAborted` 只描述目前 request；不要把它保存到 singleton 或跨 request 使用。
- 逾時與 caller cancellation 可以同時存在；用 linked source 合併兩個來源，並在使用完後 dispose：

```csharp
using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
    HttpContext.RequestAborted);
timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));

await _service.GetOrdersAsync(userId, timeoutSource.Token);
```

catch 到 `OperationCanceledException` 時，檢查 `HttpContext.RequestAborted.IsCancellationRequested`；為 `true` 才是 request 取消，否則可能是這個 5 秒逾時或其他取消來源。

## 6. 面試怎麼回答

> ASP.NET Core 的 `RequestAborted` 會在客戶端斷線、伺服器中止或 request timeout middleware 逾時時 signal。我會讓 controller 收到 `CancellationToken`，一路傳進 service、EF Core `ToListAsync(token)`、HttpClient 和 stream API。這是合作式取消：token 只發通知，實際工作必須在支援 token 的 API 或安全檢查點停止，不是強制終止執行緒。

## 7. 小練習

1. 為 repository、service、controller 的同一個 async method 加上 token。
2. 列出一個客戶端斷線後仍可能繼續做工作的 bug，並指出修正點。
3. 寫一個 loop，每處理 100 筆資料檢查一次 cancellation。
