---
title: 14 WebApplicationFactory
tags: [aspnet-core, testing, integration-test, xunit]
---

# 14 `WebApplicationFactory`：用 HTTP 做 integration test

## 學習目標

- 知道 integration test 與 unit test 的邊界。
- 使用 `WebApplicationFactory<Program>` 啟動測試用 ASP.NET Core app。
- 透過 `HttpClient` 呼叫 endpoint，而不是直接測 controller method。

## 1. 一句話理解

`WebApplicationFactory<TEntryPoint>` 會在測試 process 中建立測試 host / `TestServer`，讓你用 `HttpClient` 走過 routing、model binding、DI、middleware、serialization 與 endpoint 的整條路徑。

## 2. 先看會出事的地方

只直接呼叫 controller method，測試會通過，但 route constraint、JSON 欄位名稱、DI registration 和 middleware 都沒有被測到：

```csharp
// 這只測到一個 method，不代表 GET /api/users/{id} 可用
var result = await controller.Get(userId, CancellationToken.None);
```

`WebApplicationFactory<Program>` 會啟動測試用 host；用 `CreateClient()` 發 HTTP request，才會走過 routing、model binding、DI、middleware、serialization 和 endpoint。

## 3. C# 語法

測試專案通常加入 `Microsoft.AspNetCore.Mvc.Testing`、xUnit 與 assertion library：

```csharp
public sealed class UsersApiTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public UsersApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_unknown_user_returns_not_found()
    {
        var response = await _client.GetAsync(
            $"/api/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
```

`Program.cs` 通常要讓測試能找到 entry point：

```csharp
public partial class Program { }
```

## 4. 實務範例：覆寫 test service

真實 API 常不希望 integration test 連 production DB 或真實外部 API；可以 custom factory 替換 registration：

```csharp
public sealed class TestApplicationFactory
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                x => x.ServiceType == typeof(IUserRepository));

            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IUserRepository,
                FakeUserRepository>();
        });
    }
}
```

測試 HTTP request / JSON response：

```csharp
public sealed class UsersApiTests
    : IClassFixture<TestApplicationFactory>
{
    private readonly HttpClient _client;

    public UsersApiTests(TestApplicationFactory factory)
        => _client = factory.CreateClient();

    [Fact]
    public async Task Get_user_returns_json_dto()
    {
        var id = KnownUsers.AdaId;

        using var response = await _client.GetAsync($"/api/users/{id}");
        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<UserResponse>();

        Assert.Equal(id, dto!.Id);
        Assert.Equal("Ada", dto.Name);
    }
}
```

測試 POST 時可使用：

```csharp
var request = new CreateUserRequest("Grace", null);
using var response = await _client.PostAsJsonAsync(
    "/api/users", request);

Assert.Equal(HttpStatusCode.Created, response.StatusCode);
```

integration test 驗證的是「對 client 可見的行為」：status code、headers、JSON shape、validation、middleware、DI wiring；unit test 則驗證單一 class 的邏輯與分支，通常速度快、依賴 mock / fake。

## 5. 常見誤解

- `WebApplicationFactory` 不是 end-to-end test；它通常在 process 內跑 test server，不一定經過真實 reverse proxy、TLS、production DB。
- integration test 不是越多越好；service 細節仍適合 unit test，API contract / wiring 適合 integration test。
- 若替換 service 後仍呼叫真外部 API，測試不具 deterministic；要明確 override outbound client 或 adapter。
- 測試 factory 的 lifetime / shared state 要小心，避免測試互相污染。
- 只測 controller method 不會驗證 JSON serialization、routing constraint、model binding 或 middleware。

## 6. 面試怎麼回答

> `WebApplicationFactory<Program>` 會在測試中建立 ASP.NET Core host 與 TestServer，`CreateClient()` 回傳一個測試用 HttpClient。integration test 可以驗證 routing、DI、middleware、model binding、JSON 與 status code 的整體行為；unit test 則單獨測一個 class，用 fake / mock 隔離依賴。實務上會用 custom factory 替換 DB repository 或外部 API，保持測試 deterministic。

## 7. 小練習

1. 寫一個 GET 404 integration test。
2. 寫一個 POST 201 + JSON response test。
3. 說明為什麼 integration test 不應直接呼叫 production external API。
