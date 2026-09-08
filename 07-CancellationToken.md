---
title: 07 CancellationToken
tags: [aspnet-core, cancellation, async, http]
---

# 07 `CancellationToken`：讓 request 可以停止工作

## 學習目標

- 了解 client disconnect 與 server cancellation 如何傳入 application code。
- 將 token 一路傳到 EF Core、HttpClient、stream copy。
- 分清楚 cooperative cancellation 與強制 kill thread。

## 1. 一句話理解

`CancellationToken` 是「請你在安全的檢查點停止」的通知管道；它不會強制終止 thread，而是讓每一層合作地停止不再需要的工作。

## 2. Java 對照

Java 沒有完全相同、由 framework 到每個 API 顯式傳遞的標準型別。`CompletableFuture.cancel(true)`、thread interrupt、timeout 等有部分相似目的，但 C# 的慣用方式是把 `CancellationToken` 當成 method parameter，從 boundary 傳到 I/O operation。

## 3. C# 語法

```csharp
public async Task<IReadOnlyList<User>> GetUsersAsync(
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    return await _context.Users
        .Where(x => x.IsActive)
        .ToListAsync(cancellationToken);
}
```

`ThrowIfCancellationRequested()` 會拋出 `OperationCanceledException`；許多 BCL / EF / HttpClient API 也會在 token 被取消時自行停止並拋出 cancellation exception。

## 4. 實務範例：`RequestAborted` 一路傳遞

ASP.NET Core controller action 可以直接宣告 `CancellationToken`，framework 會以 request cancellation token binding 它：

```csharp
[HttpGet]
public Task<IReadOnlyList<UserDto>> GetUsers(
    CancellationToken cancellationToken)
    => _service.GetUsersAsync(cancellationToken);
```

在較底層也可以使用：

```csharp
var token = HttpContext.RequestAborted;
```

典型鏈路：

```text
client disconnect / server abort
        ↓
HttpContext.RequestAborted 被 signal
        ↓
controller / endpoint 的 CancellationToken
        ↓
service
        ↓
EF Core ToListAsync(token) / HttpClient GetAsync(..., token)
        ↓
DB query 或 outbound HTTP 儘早停止
```

### HttpClient 與 cancellation

```csharp
public async Task<UserProfile?> GetProfileAsync(
    Guid id,
    CancellationToken cancellationToken)
{
    using var response = await _httpClient.GetAsync(
        $"profiles/{id}", cancellationToken);

    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return null;
    }

    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<UserProfile>(
        cancellationToken);
}
```

### 自己撰寫的長工作

```csharp
public async Task ProcessAsync(
    IEnumerable<Item> items,
    CancellationToken cancellationToken)
{
    foreach (var item in items)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProcessOneAsync(item, cancellationToken);
    }
}
```

不要 catch `OperationCanceledException` 後當成一般 error log；要依 application policy 決定是否重新拋出、回 499-like semantics，或讓 ASP.NET Core 結束 request。

## 5. 常見誤解

- cancellation 不會 kill thread，也不會回滾已經完成的副作用。
- 收到 token 不代表 method 一定會停；底層 API 必須支援它，或你要自行在 loop / checkpoint 檢查。
- 不要在 service 裡無視 caller token，另建一個永不取消的 token，除非這是明確的 background work 設計。
- `HttpContext.RequestAborted` 只描述目前 request；不要把它保存到 singleton 或跨 request 使用。
- timeout 與 cancellation 可以同時存在：timeout 是一種取消來源，但 domain 也可能有自己的 `CancellationTokenSource`。

## 6. 面試怎麼回答

> ASP.NET Core 的 `RequestAborted` 會在 client disconnect 或 server abort 時 signal。我會讓 controller 收到 `CancellationToken`，一路傳進 service、EF Core `ToListAsync(token)`、HttpClient 和 stream API。這是 cooperative cancellation：token 只發通知，實際工作必須在支援 token 的 API 或安全檢查點停止，不是強制 kill thread。

## 7. 小練習

1. 為 repository、service、controller 的同一個 async method 加上 token。
2. 列出一個 client disconnect 後仍可能繼續做工作的 bug，並指出修正點。
3. 寫一個 loop，每處理 100 筆資料檢查一次 cancellation。
