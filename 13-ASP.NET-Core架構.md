---
title: 13 ASP.NET Core 架構
tags: [aspnet-core, middleware, mvc, web-api, controllers, minimal-api]
---

# 13 ASP.NET Core 基礎架構

## 學習目標

- 看懂現代 `Program.cs` 的 host、DI 與 middleware pipeline。
- 串起 request → endpoint → service → repository / HttpClient → DTO → JSON。
- 分辨 MVC controller、API controller 與 Minimal API 的角色，理解它們共用同一套 ASP.NET Core 基礎。
- 知道 MVC 產生 HTML 與 Web API 回 JSON 是兩條不同的應用邊界。

## 1. 一句話理解

ASP.NET Core app 是由 host 啟動、由 DI 組裝、由 middleware 依序處理 HTTP request，最後把 request routing 到 MVC view、API controller 或 endpoint，再由 framework 產生 HTML 或 JSON response 的 pipeline。

## 2. Java 對照

| Spring Boot | ASP.NET Core | 備註 |
| --- | --- | --- |
| Servlet `Filter` | middleware | 依序包住 request，能在 `next` 前後執行 |
| `HandlerInterceptor` | MVC action filter／`AddEndpointFilter` | 更接近 endpoint 或 action 層 |
| `DispatcherServlet` | routing + endpoint middleware | 找到 controller 或 Minimal API endpoint |
| `@RestController` | `[ApiController]` | JSON API controller |
| Spring Boot auto-configuration | `WebApplication.CreateBuilder` 預設 | 建立 host、設定、logging 與 DI 基礎 |
| `@Scheduled`／`ApplicationRunner` | `IHostedService`／`BackgroundService` | 應用程式啟動後的背景工作 |

## 3. C# 語法

### 先看會出事的地方

只寫 `AddControllers()`，沒有寫 `MapControllers()`，MVC 服務雖然已註冊，`GET /api/users/{id}` 仍然找不到 endpoint：

```csharp
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers(); // 把 attribute-routed controller 接進 request pipeline
app.Run();
```

ASP.NET Core app 是一條由 host、DI、middleware 和 endpoint 組成的 request pipeline；註冊 service 與 map endpoint 是不同責任。

如果只用 Web API 的 `ControllerBase → Ok → JSON` 來理解 ASP.NET Core，會漏掉 MVC 專案最常見的另一條路：`Controller → ViewModel → Razor View → HTML`。MVC、Razor、表單、validation 和 CRUD 會在 [[13-ASP.NET-Core-MVC與Razor-Views]] 與 [[15-ASP.NET-Core-MVC-CRUD]] 展開。

### 現代 `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
```

`WebApplication.CreateBuilder` 會準備 host、configuration、logging、DI，預設伺服器是 Kestrel；設定來源依序包含 `appsettings.json`、`appsettings.{Environment}.json`、Development 的 user secrets、環境變數與 command line。`builder.Services` 在 build 前註冊 service；`app` build 後定義 middleware 與 endpoint。

### middleware

```csharp
app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    await next(); // 交給下一個 middleware
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
2. 呼叫 `next` 把 request 交給下一個 middleware。
3. 在 `next` 後做事。
4. 不呼叫 `next`，直接短路回應。

順序重要：exception handling 放前面；`UseRouting()` 在 `UseAuthentication()`、`UseAuthorization()` 前；`Map*` 放最後。`WebApplication` 若沒有明寫 `UseRouting()`，會自動把 routing 放進 pipeline；一旦明寫，authentication 與 authorization 就必須放在它之後。

### `Use`、`Run`、`Map`

- `Use` 建立 middleware，可在 `next` 前後執行，或不呼叫 `next` 直接短路。
- `Run` 建立終端 middleware，不會再呼叫下一個 middleware；`app.Run()` 則是啟動 host 的 `WebApplication.Run()`，兩者名稱相同但位置不同。
- `Map` 依 path 或 endpoint metadata 分支；`MapGet`、`MapControllers` 是把 endpoint 加入 routing data source。

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

`AddControllers()` 預設不會把 controller 類別註冊成 DI service；`DefaultControllerActivator` 會建立 controller，constructor 參數才由 DI 解析。需要由 container 建立 controller 時，另用 `AddControllersAsServices()`。

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

Minimal API 的 parameter binding 也可以從 DI、route、query、body 等來源取得值。它仍走同一套 routing、binding 與 DI，只是端點直接寫成 lambda。新專案官方建議優先使用 Minimal API；需要自訂 model binder、`IModelValidator`、application parts 或 OData 時，再選 controller。

### hosted service

`IHostedService`／`BackgroundService` 在 host 啟動後執行背景工作；它們通常是 singleton，需要 scoped service 時要像 [[09-DependencyInjection]] 的範例一樣用 `IServiceScopeFactory` 建立 scope。

## 4. 實務範例：完整 request flow

```text
HTTP Request
    ↓
Kestrel / host
    ↓
    Exception handling → HTTPS redirection → routing → authentication → authorization
    ↓
Controller / Minimal API endpoint
    ↓
    framework 建立 controller，constructor 參數由 DI 解析
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

`examples/UserApi.Tests` 以 `WebApplicationFactory<Program>` 驗證這條 pipeline：匿名 request 回 401、只有身份沒有 `users.read` claim 回 403、帶正確 claim 才能到達 endpoint 並回 200。

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

`{id:guid}` 是 route constraint：`GET /api/users/abc` 不匹配，回 404，action 不會執行。它用來區分相似 route，不是輸入驗證；若需求是回 400，應讓參數進入 model binding，再由 `[ApiController]` 處理驗證錯誤。

實際結果：

```text
GET /api/users/abc → 404 Not Found
```

設定與 options：

```csharp
using System.ComponentModel.DataAnnotations;

public sealed class UserApiOptions
{
    public const string SectionName = "UserApi";
    [Required, Url]
    public string BaseUrl { get; init; } = string.Empty;
}

builder.Services
    .AddOptions<UserApiOptions>()
    .Bind(builder.Configuration.GetSection(UserApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

`appsettings.json`、環境變數、command line 等 configuration providers 會被 host 組合；敏感資料不要硬編碼在 JSON，使用 secret store / environment / managed identity 等部署策略。

### exception handling

```csharp
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
```

無參數的 `UseExceptionHandler()` 要搭配 `AddProblemDetails()` 或 `AddExceptionHandler<T>()`；缺少 fallback 時啟動會拋出：

```text
System.InvalidOperationException: An error occurred when configuring the exception handler middleware. Either the 'ExceptionHandlingPath' or the 'ExceptionHandler' property must be set in 'UseExceptionHandler()'.
```

domain layer 可以拋出明確 exception（例如 `UserNotFoundException`），由 `IExceptionHandler` 集中 mapping 成 `ProblemDetails`。不要在每個 action 重複 `try/catch (Exception)` 然後回傳 500 字串，否則會遺失一致的格式、log 與 `traceId`。

## 5. 常見誤解

- `Program.cs` 同時是 composition root 與 middleware pipeline 定義。
- middleware 順序是功能契約：`UseAuthorization()` 必須在 routing 與 authentication 之後。
- controller 不應承擔所有 DB query、外部 API、mapping、domain rule；它比較像 HTTP adapter。
- Minimal API endpoint 應把大型 domain 邏輯抽到 service／handler。
- 只有 `AddControllers()` 時，`POST /api/users` 仍回 404；`MapControllers()` 才把 attribute-routed controllers 加入 pipeline。

## 6. 面試怎麼回答

> 現代 ASP.NET Core 以 `WebApplicationBuilder` 建 host，在 `builder.Services` 註冊 DI，`app` build 後設定 middleware 與 endpoint。request 先經 middleware chain，再被 routing 送到 controller 或 Minimal API；endpoint 透過 DI 呼叫 service、repository、EF Core 或 HttpClient，最後由 `System.Text.Json` 把 DTO serialize 成 response。Middleware 順序很重要，exception handling、authentication、authorization 都有明確的 pipeline 位置。

## 7. 小練習

1. 在 `Program.cs` 寫出 controllers、ProblemDetails、user service 的 registration。
2. 解釋 `MapControllers()` 與 `AddControllers()` 的差別。
3. 將一個 controller action 拆成 controller → service → repository 三層。
