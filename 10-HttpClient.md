---
title: 10 HttpClient
tags: [aspnet-core, http, httpclient, json]
---

# 10 `HttpClient` 與 `IHttpClientFactory`

## 學習目標

- 能讀懂對外 HTTP 呼叫的 request、response 與 JSON 流程。
- 理解為什麼 .NET 不建議每次 request 都 `new HttpClient()`。
- 能使用 named client 或 typed client，正確傳遞 timeout 與 cancellation。

## 1. 一句話理解

`HttpClient` 是 HTTP API 的高階 client；`IHttpClientFactory` 把 client 設定、handler 生命週期、記錄與 DI 註冊集中管理，避免手動建立 client 造成 socket／DNS 問題。

## 2. Java 對照

| Java／Spring | .NET | 備註 |
| --- | --- | --- |
| JDK 11+ `java.net.http.HttpClient` | `HttpClient` | 都應重用 instance，不要每個 request 建立一個 |
| Spring `RestClient` | typed／named client | 都可把 base address、headers 與 endpoint 封裝起來 |
| Spring `WebClient` | `HttpClient` + async API | 都能以非同步方式呼叫外部服務，但抽象層不同 |

Spring Framework 7 已將 `RestTemplate` 標為 deprecated，新的同步 fluent API 是 `RestClient`。Spring 沒有 `IHttpClientFactory` 的 handler lifetime 輪換概念；DNS 與連線生命週期由底層 HTTP client 管理。

## 3. C# 語法

### 先看會出事的地方

每個 request 都 `new HttpClient()`，短時間測試看不出差別；流量上來後，連線無法穩定重用，還可能遇到 socket exhaustion。問題在 client 的建立與 handler lifetime，不在 GET 語法：

```csharp
// 不要在每個 request 中反覆建立並 dispose HttpClient
using var client = new HttpClient();
var response = await client.GetAsync("https://profiles.example.com/users/1");
```

`.NET` 透過 `IHttpClientFactory` 管理 handler 生命週期、集中設定與 DI 註冊；應用程式碼再用 typed client 把外部 API 的路徑與回應型別包起來。

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

public sealed record ProfileDto(Guid Id, string DisplayName);
public sealed record CreateProfileRequest(string DisplayName);
public sealed record ChargeRequest(decimal Amount, string Currency);
```

### registration：typed client

```csharp
builder.Services.AddHttpClient<ProfileClient>(client =>
{
    client.BaseAddress = new Uri("https://profiles.example.com/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("UserApi/1.0");
}).SetHandlerLifetime(TimeSpan.FromMinutes(2));
```

### GET JSON

```csharp
public sealed class ProfileClient(HttpClient httpClient)
{
    public async Task<ProfileDto?> GetAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"profiles/{userId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProfileDto>(
            cancellationToken);
    }
}
```

### POST JSON

以下方法加入前面的 `ProfileClient`：

```csharp
public async Task<ProfileDto> CreateAsync(
    CreateProfileRequest request,
    CancellationToken cancellationToken)
{
    using var response = await httpClient.PostAsJsonAsync(
        "profiles", request, cancellationToken);

    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<ProfileDto>(
        cancellationToken))!;
}
```

需要額外 headers、不同 HTTP method 或串流時，使用 `HttpRequestMessage`：

```csharp
using var message = new HttpRequestMessage(
    HttpMethod.Get, $"profiles/{userId}");
message.Headers.Accept.Add(
    new MediaTypeWithQualityHeaderValue("application/json"));

using var response = await httpClient.SendAsync(
    message,
    HttpCompletionOption.ResponseHeadersRead,
    cancellationToken);

response.EnsureSuccessStatusCode();
var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(
    cancellationToken);
```

`ResponseHeadersRead` 只等到 headers 讀到；body 的 `ReadFromJsonAsync` 仍要使用同一個 `CancellationToken`，不能只依賴 `HttpClient.Timeout`。

## 4. 實務範例：named client 與 typed client

### named client

```csharp
builder.Services.AddHttpClient("BillingApi", client =>
{
    client.BaseAddress = new Uri("https://billing.example.com/");
});

public sealed class BillingService(IHttpClientFactory factory)
{
    public async Task ChargeAsync(
        ChargeRequest request,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateClient("BillingApi");
        using var response = await client.PostAsJsonAsync(
            "charges", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
```

### .NET 8+ resilience handler

`Microsoft.Extensions.Http.Resilience` 提供標準 resilience pipeline。它預設最多 retry 3 次、每次嘗試 timeout 10 秒、整條 pipeline timeout 30 秒；不安全的 HTTP method 應依業務語意停用 retry：

```csharp
builder.Services
    .AddHttpClient<ProfileClient>()
    .AddStandardResilienceHandler(options =>
        options.Retry.DisableForUnsafeHttpMethods());
```

這個設定在 .NET 8+ 使用 `Microsoft.Extensions.Http.Resilience` 套件。`HttpClient.Timeout` 還會包住整條 pipeline；例如設定成 5 秒時，會比 30 秒的 resilience total timeout 先中止整個呼叫。

### 選擇建議

- typed client：一個 class 封裝一個外部 API，base address、headers、retry 設定與 endpoint 方法集中，通常是推薦的寫法。
- named client：同一個 service 需要多組設定，或呼叫端想用名稱選擇 client。
- 直接注入 `HttpClient`：`AddHttpClient()` 會提供 factory 的 default client；簡單情境可用。

官方有兩條主要路徑：需要多組設定、DI 與記錄時使用 `IHttpClientFactory`；單一設定也可以重用 static／singleton client，並設定 `SocketsHttpHandler.PooledConnectionLifetime` 讓連線定期重新解析 DNS：

```csharp
public static class SharedProfileClient
{
    public static readonly HttpClient Client = new(
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
}
```

`IHttpClientFactory` 的 handler 預設每 2 分鐘輪換一次，可用 `SetHandlerLifetime` 調整。factory 建立的 `HttpClient` 即使 dispose，也不會連帶 dispose handler；真正要避免的是每個 request 自己建立新的 client，重現時常見大量 `TIME_WAIT` 與 `SocketException`。

typed client 與直接注入的 `HttpClient` 都是 transient，不要被 singleton constructor 接住；如果 singleton 必須動態取得 client，注入 `IHttpClientFactory`、每次 `CreateClient`。若仍要在 singleton 使用 typed client，改用 `SocketsHttpHandler` 設定 `PooledConnectionLifetime` 的 primary handler。

## 5. 常見誤解

- `EnsureSuccessStatusCode()` 把 200–299 以外轉成 `HttpRequestException`；業務邏輯要先判斷 404／409，或在 catch 中讀 `ex.StatusCode`。
- `HttpClient.Timeout` 預設是 100 秒；在預設 `ResponseContentRead` 下涵蓋 headers 與 body，使用 `ResponseHeadersRead` 時只涵蓋到 headers，body 必須靠 `CancellationToken` 設定上限。
- JSON 的 GET／POST 便利方法來自 `System.Net.Http.Json`，不是 `HttpClient` 本身。
- 每個外部 API 都要決定 timeout、retry、冪等性、狀態碼對應與記錄；標準 resilience handler 預設會 retry 所有 HTTP method，POST 等不安全 method 要明確停用。
- 呼叫外部 API 時不要把 bearer token、個資或 request body 無限制寫進 log。

實際的 `EnsureSuccessStatusCode()` 例外：

```text
System.Net.Http.HttpRequestException: Response status code does not indicate success: 500 (Internal Server Error).
```

`GetFromJsonAsync<T>` 也會對非成功 status code 拋 `HttpRequestException`；需要把 404 轉成 `null` 時，使用本章的 `GetAsync`、先判斷 `StatusCode`，再讀 JSON。

`HttpClient.Timeout` 與 caller cancellation 都可能得到 `TaskCanceledException`。實際訊息可用來對照來源：

```text
System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 0.5 seconds elapsing. | inner=System.TimeoutException
System.Threading.Tasks.TaskCanceledException: A task was canceled.
```

## 6. 面試怎麼回答

> .NET 通常透過 `IHttpClientFactory` 管理 `HttpClient`。它集中設定 base address、headers、handler 生命週期與記錄，也避免每次 request 建立新 client 導致 connection／socket 與 DNS 問題。對一個明確的外部 API，我會用 typed client；多組設定則可用 named client。每個呼叫都要傳 `CancellationToken`、檢查 status code，並依冪等性設計 timeout／retry。

## 7. 小練習

1. 寫一個 typed client 呼叫 `GET /profiles/{id}`。
2. 在 POST 呼叫中加入 cancellation 與 `EnsureSuccessStatusCode()`。
3. 解釋 typed client 被 singleton capture 的風險。
