---
title: 10 HttpClient
tags: [aspnet-core, http, httpclient, json]
---

# 10 `HttpClient` 與 `IHttpClientFactory`

## 學習目標

- 能讀懂 outbound HTTP call 的 request / response / JSON flow。
- 理解為什麼 ASP.NET Core 不建議每次 request 都 `new HttpClient()`。
- 能使用 named client 或 typed client，正確傳遞 timeout 與 cancellation。

## 1. 一句話理解

`HttpClient` 是 HTTP API 的高階 client；`IHttpClientFactory` 把 client 的設定、handler lifetime、logging 與 DI wiring 集中管理，避免手動建立 client 造成 socket / DNS 問題。

## 2. 先看會出事的地方

每個 request 都 `new HttpClient()`，短時間測試看不出差別；流量上來後，連線無法穩定重用，還可能遇到 socket exhaustion。問題在 client 的建立與 handler lifetime，不在 GET 語法：

```csharp
// 不要在每個 request 中反覆建立並 dispose HttpClient
using var client = new HttpClient();
var response = await client.GetAsync("https://profiles.example.com/users/1");
```

ASP.NET Core 以 `IHttpClientFactory` 管理 handler lifetime、集中設定與 DI wiring；application code 再用 typed client 把外部 API 的路徑與回應型別包起來。

## 3. C# 語法

### registration：typed client

```csharp
builder.Services.AddHttpClient<ProfileClient>(client =>
{
    client.BaseAddress = new Uri("https://profiles.example.com/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("UserApi/1.0");
});
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
```

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

### 選擇建議

- typed client：一個 class 封裝一個外部 API，base address、headers、retry policy 與 endpoint 方法集中，通常是推薦的 application code 形狀。
- named client：同一個 service 需要多組設定，或呼叫端想用名稱選擇 client。
- 直接注入 `HttpClient`：ASP.NET Core 會用 factory 的 default client；簡單情境可用。

為什麼不每個 request `new HttpClient()`？反覆建立與 dispose 可能造成 socket exhaustion，且底層 handler / connection 不容易重用；長期持有一個 static client 又可能讓 DNS 更新不生效。`IHttpClientFactory` 管理 handler lifetime，提供較安全的預設方案。

注意：typed client 應是短生命物件，不要把 typed client capture 在 singleton 中；如果 singleton 必須動態取得 client，考慮注入 `IHttpClientFactory`、每次 `CreateClient`。

## 5. 常見誤解

- `EnsureSuccessStatusCode()` 不是「所有錯誤都該吞掉」；它只是把非 2xx 轉成 exception，domain 仍可針對 404 / 409 做明確處理。
- `HttpClient.Timeout` 是整體 timeout；request cancellation token 可以再提供 caller-driven cancellation。
- `HttpClient` 不等於 JSON client；JSON 是 `System.Net.Http.Json` 或 serializer 的功能。
- 每個外部 API 都要決定 timeout、retry、idempotency、status mapping 與 logging；不要無腦 retry POST。
- 呼叫外部 API 時不要把 bearer token、個資或 request body 無限制寫進 log。

## 6. 面試怎麼回答

> ASP.NET Core 通常透過 `IHttpClientFactory` 管理 `HttpClient`。它集中設定 base address、headers、handler lifetime、logging，也避免每次 request new client 導致 connection / socket 與 DNS 問題。對一個明確的外部 API，我會用 typed client；多組設定則可用 named client。每個呼叫都要傳 `CancellationToken`、檢查 status code，並依 idempotency 設計 timeout / retry。

## 7. 小練習

1. 寫一個 typed client 呼叫 `GET /profiles/{id}`。
2. 在 POST 呼叫中加入 cancellation 與 `EnsureSuccessStatusCode()`。
3. 解釋 typed client 被 singleton capture 的風險。
