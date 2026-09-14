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

`WebApplicationFactory<TEntryPoint>` 會在測試 process 中建立測試 host／`TestServer`；`CreateClient()` 發出的 request 會走過 routing、model binding、DI、middleware、serialization 與 endpoint。

## 2. Java 對照

| Spring Boot | ASP.NET Core | 備註 |
| --- | --- | --- |
| `@SpringBootTest(webEnvironment = MOCK)` + `MockMvc` | `WebApplicationFactory<Program>` + `TestServer` | 都在記憶體內跑完整 application pipeline，不開 port |
| `webEnvironment = RANDOM_PORT` + `TestRestTemplate` | .NET 10 `UseKestrel()` + `CreateClient()` | 都開真正的 HTTP port，適合需要真 HTTP 的測試 |
| `@MockitoBean` | `ConfigureServices`／`ConfigureTestServices` | 在測試 host 中替換 service registration |

`TestServer` 會走 ASP.NET Core middleware pipeline；`MockMvc` 則是在 Spring MVC 層 mock，不經 servlet container。

## 3. C# 語法

### 先看會出事的地方

只直接呼叫 controller method，測試會通過，但 route constraint、JSON 欄位名稱、DI registration 和 middleware 都沒有被測到：

```csharp
// 這只測到一個 method，不代表 GET /api/users/{id} 可用
var result = await controller.Get(userId, CancellationToken.None);
```

若 route 是 `"/api/users/{id:guid}"`，單元測試可以傳入任意 `int` 而通過；真正的 HTTP request `GET /api/users/1` 會因 route constraint 不匹配而回 404：

```text
GET /api/users/1 → 404 Not Found
```

`WebApplicationFactory<Program>` 會啟動測試用 host；用 `CreateClient()` 發 HTTP request，才會走過 routing、model binding、DI、middleware、serialization 和 endpoint。

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

`.NET 10` 不需要在 `Program.cs` 手寫 `public partial class Program { }`；SDK 會自動產生 public entry point。`.NET 6–9` 才需要這段，或在測試專案使用 `InternalsVisibleTo`。

```csharp
// .NET 10：不需要額外宣告 Program。
```

## 4. 實務範例：覆寫 test service

真實 API 常不希望 integration test 連 production DB 或真實外部 API；可以 custom factory 替換 registration：

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;

public sealed class TestApplicationFactory
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserRepository>();
            services.AddSingleton<IUserRepository,
                FakeUserRepository>();
        });
    }
}
```

若 SUT 直接註冊 `AppDbContext` 而沒有 repository 介面，測試可以在 factory 裡移除 production 的 options／connection registration，再注入保持開啟的 SQLite in-memory connection：

```csharp
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

public sealed class SqliteApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection =
        new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.RemoveAll<DbConnection>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(_connection));
        });
    }
}
```

`Microsoft.EntityFrameworkCore.InMemory` 官方不建議作為一般測試 provider；SQLite in-memory 比較接近關聯式資料庫，但大小寫、SQL 函式與 transaction 行為仍可能和 SQL Server 不同。要驗證 production SQL 語意，使用真實 SQL Server（例如 Testcontainers）更可靠。

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

integration test 驗證的是 client 可見的行為：status code、headers、JSON 結構、validation、middleware 與 DI 註冊；unit test 則驗證單一 class 的邏輯與分支，通常速度快、依賴 mock／fake。

預設 factory 會照 SUT 的 `Program.cs` 註冊啟動，未指定環境時是 `Development`；要換掉資料庫或外部服務，使用上面的 custom factory。repo 內的驗證命令與實際輸出：

```bash
dotnet test examples/UserApi.Tests/UserApi.Tests.csproj --configuration Release
```

```text
失敗: 0，通過: 3，略過: 0，總計: 3
```

## 5. 常見誤解

- `WebApplicationFactory` 預設在 process 內使用記憶體中的 `TestServer`，不開 port、不走 TLS 或 reverse proxy；.NET 10 起可用 `UseKestrel()` 改成真正的 Kestrel port。
- integration test 不是越多越好；service 細節仍適合 unit test，API contract / wiring 適合 integration test。
- 若替換 service 後仍呼叫真外部 API，結果不固定；要把對外呼叫的 `HttpClient` 或 adapter 一併換成 fake。
- `IClassFixture` 讓同一測試類別共用一個 factory、host 與 DI container；singleton fake 的資料會跨測試保留，POST 建立的 Grace 可能被下一個測試看到。

## 6. 面試怎麼回答

> `WebApplicationFactory<Program>` 會在測試中建立 ASP.NET Core host 與 TestServer，`CreateClient()` 回傳一個測試用 HttpClient。integration test 可以驗證 routing、DI、middleware、model binding、JSON 與 status code；unit test 則單獨測一個 class，用 fake／mock 隔離依賴。實務上會用 custom factory 替換 DB repository 或外部 API，讓結果可重現。

## 7. 小練習

1. 寫一個 GET 404 integration test。
2. 寫一個 POST 201 + JSON response test。
3. 說明為什麼 integration test 不應直接呼叫 production external API。
