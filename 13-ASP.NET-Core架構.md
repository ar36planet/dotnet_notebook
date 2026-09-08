---
title: 13 ASP.NET Core 架構
tags: [aspnet-core, middleware, web-api, controllers, minimal-api]
---

# 13 ASP.NET Core 基礎架構

## 學習目標

- 看懂現代 `Program.cs` 的 host、DI 與 middleware pipeline。
- 串起 request → endpoint → service → repository / HttpClient → DTO → JSON。
- 分辨 controller 與 Minimal API 的角色，理解它們共享的 ASP.NET Core foundation。

## 1. 一句話理解

ASP.NET Core app 是由 host 啟動、由 DI 組裝、由 middleware 依序處理 HTTP request，最後把 request routing 到 controller 或 endpoint，再由 framework serialize response 的 pipeline。

## 2. Java 對照

| ASP.NET Core | Spring Boot 大致對照 |
| --- | --- |
| `WebApplicationBuilder` / `WebApplication` | Spring application bootstrap / embedded server |
| `Program.cs` | `main` + configuration / bean setup 的集中入口 |
| middleware | filter / interceptor / web middleware |
| controller | `@RestController` |
| `builder.Services` | ApplicationContext bean registrations |
| `appsettings.json` + IConfiguration | `application.yml` / property sources |
| Minimal API | function-style route handler；不是另一個 runtime |

## 3. C# 語法

### 現代 `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddUserModule();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
```

`WebApplication.CreateBuilder` 會準備 host、configuration、logging、DI；`builder.Services` 在 build 前註冊 service；`app` build 後定義 middleware 與 endpoint。

### middleware

```csharp
app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    await next();
    var elapsed = Stopwatch.GetElapsedTime(started);
    app.Logger.LogInformation(
        "{Method} {Path} took {ElapsedMs}ms",
        context.Request.Method,
        context.Request.Path,
        elapsed.TotalMilliseconds);
});
```

每個 middleware 可以：

1. 在 `next` 前做事。
2. 呼叫 `next` 把 request 交給後面的 component。
3. 在 `next` 後做事。
4. 不呼叫 `next`，直接短路回應。

順序重要：exception handling 應在較前面，authentication 要在 authorization 前，routing / endpoint mapping 需符合使用的 hosting model。

### controller

```csharp
[ApiController]
[Route("api/users")]
public sealed class UsersController(IUserService service) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserResponse>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var user = await service.GetAsync(id, cancellationToken);
        return user is null ? NotFound() : Ok(user);
    }
}
```

### Minimal API

```csharp
app.MapGet("/api/users/{id:guid}", async (
    Guid id,
    IUserService service,
    CancellationToken cancellationToken) =>
{
    var user = await service.GetAsync(id, cancellationToken);
    return user is null ? Results.NotFound() : Results.Ok(user);
});
```

Minimal API 的 parameter binding 也可以從 DI、route、query、body 等來源取得值；它不是「沒有 framework」，只是 endpoint declaration 更接近 route handler。大型專案可依 team convention 選 controller、Minimal API 或混用。

## 4. 實務範例：完整 request flow

```text
HTTP Request
    ↓
Kestrel / host
    ↓
Exception handling → HTTPS → auth → routing middleware
    ↓
Controller / Minimal API endpoint
    ↓
DI 建立 controller 與 service
    ↓
Service：domain rule、LINQ、CancellationToken
    ↓
Repository / EF Core / HttpClient
    ↓
SQL Server / external API
    ↓
DTO / ActionResult
    ↓
System.Text.Json serialization
    ↓
HTTP Response
```

一個簡化的 `GET` + `POST` controller：

```csharp
[ApiController]
[Route("api/users")]
public sealed class UsersController(IUserService service)
    : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserResponse>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }
}
```

上段的 route constraint 應寫成 `[HttpGet("{id:guid}")]`；它用 route syntax 告訴 framework 只有合法 Guid 才匹配。

設定與 options：

```csharp
public sealed class UserApiOptions
{
    public const string SectionName = "UserApi";
    public string BaseUrl { get; init; } = string.Empty;
}

builder.Services
    .AddOptions<UserApiOptions>()
    .Bind(builder.Configuration.GetSection(UserApiOptions.SectionName))
    .ValidateDataAnnotations();
```

`appsettings.json`、環境變數、command line 等 configuration providers 會被 host 組合；敏感資料不要硬編碼在 JSON，使用 secret store / environment / managed identity 等部署策略。

### exception handling

```csharp
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
```

domain layer 可以拋出明確 exception（例如 `UserNotFoundException`），由集中式 exception handler mapping 成 `ProblemDetails`。不要在每個 action 重複 `try/catch (Exception)` 然後回傳 500 字串，否則會遺失一致性、logging 與 correlation context。

## 5. 常見誤解

- `Program.cs` 不是只有 `main`；它同時是 composition root 與 middleware pipeline 定義。
- middleware 是有順序的 chain；`UseAuthorization()` 放在錯誤位置可能造成安全或功能問題。
- controller 不應承擔所有 DB query、外部 API、mapping、domain rule；它比較像 HTTP adapter。
- Minimal API 不是把所有邏輯寫在 endpoint lambda；大型 domain 仍應抽到 service / handler。
- `AddControllers()` 只註冊 MVC controller services；`MapControllers()` 才把 attribute-routed controllers map 到 pipeline。

## 6. 面試怎麼回答

> 現代 ASP.NET Core 以 `WebApplicationBuilder` 建 host，在 `builder.Services` 註冊 DI，`app` build 後設定 middleware 與 endpoint。request 先經 middleware chain，再被 routing 送到 controller 或 Minimal API；endpoint 透過 DI 呼叫 service、repository、EF Core 或 HttpClient，最後由 `System.Text.Json` 把 DTO serialize 成 response。Middleware 順序很重要，exception handling、authentication、authorization 都有明確的 pipeline 位置。

## 7. 小練習

1. 在 `Program.cs` 寫出 controllers、ProblemDetails、user service 的 registration。
2. 解釋 `MapControllers()` 與 `AddControllers()` 的差別。
3. 將一個 controller action 拆成 controller → service → repository 三層。
