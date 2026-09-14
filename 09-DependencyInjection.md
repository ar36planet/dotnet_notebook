---
title: 09 Dependency Injection
tags: [aspnet-core, di, ioc]
---

# 09 Dependency Injection：物件的依賴由 container 建立

## 學習目標

- 看懂 ASP.NET Core 內建 DI 如何建立物件。
- 分辨建構子注入、IoC container 與 service lifetime。
- 知道 Singleton / Scoped / Transient 的實際風險。

## 1. 一句話理解

ASP.NET Core DI container 依照 `Program.cs` 的註冊與生命週期建立物件，把建構子需要的依賴一併注入，class 不必自己 `new` 協作者。

## 2. Java 對照

| Spring | ASP.NET Core DI | 備註 |
| --- | --- | --- |
| singleton（預設） | `AddSingleton` | 每個 container 一份 |
| prototype | `AddTransient` | 每次解析建立一份 |
| request scope | `AddScoped` | 每個 HTTP request scope 一份 |
| `@Autowired` | constructor injection | .NET 內建 container 不支援 property injection |
| `ObjectFactory<T>`／scoped proxy | `IServiceScopeFactory` | singleton 需要 scoped service 時建立明確 scope |

Spring 未指定 scope 時預設 singleton；.NET 每次註冊都要明確選 lifetime。

## 3. C# 語法

### 先看會出事的地方

`DbContext` 是 scoped，卻被 singleton service 保存起來。Development 環境的 scope validation 會在 `Build()` 直接拒絕這個依賴圖：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ReportCache>();
builder.Services.AddScoped<AppDbContext>();

builder.Build();

public sealed class ReportCache(AppDbContext db)
{
    private readonly AppDbContext _db = db;
    // singleton 抓住 scoped：lifetime 不相容（captive dependency）
}
```

實際輸出：

```text
Unhandled exception. System.AggregateException: Some services are not able to be constructed (Error while validating the service descriptor 'ServiceType: ReportCache Lifetime: Singleton ImplementationType: ReportCache': Cannot consume scoped service 'AppDbContext' from singleton 'ReportCache'.)
 ---> System.InvalidOperationException: Cannot consume scoped service 'AppDbContext' from singleton 'ReportCache'.
```

Production 預設不開 scope validation；錯誤設定可能靜默通過，讓同一個 `DbContext` 被所有 request 共用。`DbContext` 不是 thread-safe，併發操作可能拋出：

```text
System.InvalidOperationException: A second operation was started on this context instance before a previous operation completed.
```

若要在 Production 也提早擋住，可在 host builder 開啟 `ValidateScopes` 與 `ValidateOnBuild`。被注入的物件不能比使用它的 service 短命：singleton 只能依賴 singleton；scoped 可依賴 scoped 與 singleton；transient 可以依賴三者。

```csharp
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});
```

### 註冊

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<EmailFormatter>();

// 同一個介面有多個實作時，.NET 8+ 可用 keyed services。
builder.Services.AddKeyedScoped<INotificationSender, EmailNotificationSender>("email");
builder.Services.AddKeyedScoped<INotificationSender, SmsNotificationSender>("sms");

// reusable module 不應覆寫呼叫端已註冊的實作。
builder.Services.TryAddScoped<IOrderRepository, OrderRepository>();
```

### 建構子注入

```csharp
public sealed class OrderService(
    IOrderRepository repository,
    TimeProvider clock) : IOrderService
{
    public Task<OrderDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        // primary constructor parameters 可在 class member 使用。
        return repository.FindDtoAsync(id, clock.GetUtcNow(), cancellationToken);
    }
}
```

上面使用 primary constructor（C# 12）。如果需要明確看到欄位，也可以寫成傳統形式：

```csharp
public sealed class OrderService : IOrderService
{
    private readonly IOrderRepository _repository;
    private readonly TimeProvider _clock;

    public OrderService(IOrderRepository repository, TimeProvider clock)
    {
        _repository = repository;
        _clock = clock;
    }
}
```

### 三種 lifetime

| 註冊方式 | 意義 | 常見用途 | 風險 |
| --- | --- | --- | --- |
| `AddSingleton` | container lifetime 只有一個實例 | 無狀態的共用服務、不可變快取、時鐘 | 只能依賴 singleton；執行緒安全要自己保證 |
| `AddScoped` | 每個 HTTP request 一個 scope 內一份 | `DbContext`、應用層服務、工作單位 | 不要讓更長壽命的 singleton 抓住它 |
| `AddTransient` | 每次 resolve 產生新實例 | 輕量、無狀態的格式化器 | 實作 `IDisposable` 的型別不要註冊成 transient；root provider 解析到的 disposable transient 會被容器抓到 app 關閉 |

被依賴的物件不能比使用它的 service 短命。ASP.NET Core 的 request scope 是一個 HTTP request；EF Core `DbContext` 由 `AddDbContext` 預設註冊為 scoped。

`BackgroundService` 本身是 singleton；它需要 `DbContext` 時，注入 `IServiceScopeFactory`，在每次工作中建立 scope：

```csharp
public sealed class NightlyOrderReportWorker(IServiceScopeFactory scopeFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = await db.Orders.CountAsync(stoppingToken);
    }
}
```

## 4. 實務範例：controller → service → repository

```csharp
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

[ApiController]
[Route("api/orders")]
public sealed class OrdersController(IOrderService service)
    : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDto>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
```

為什麼常見 interface + implementation + 建構子注入？

- interface 讓上層只依賴契約，實作可替換。
- constructor 清楚列出依賴，建構完成就是可用狀態。
- test 可以注入 fake / mock，不必碰真 DB。
- lifetime 由 `Program.cs` 一處決定，不散落在各 class。

但 interface 不是宗教：只有當有替換邊界、外部資源或測試隔離時才值得建立；為每個只有一個簡單實作的 class 無腦加 interface，只會多一層要跳的檔案。

### 把註冊收進 extension method

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrderModule(
        this IServiceCollection services)
    {
        services.TryAddScoped<IOrderService, OrderService>();
        services.TryAddScoped<IOrderRepository, EfOrderRepository>();
        return services;
    }
}

// Program.cs
builder.Services.AddOrderModule();
```

## 5. 常見誤解

- DI container 與 service locator 不同；業務程式碼不應到處注入 `IServiceProvider` 再自行 resolve。
- `Scoped` 是每個 scope 一份；web request 通常各自有 scope。
- `Singleton` 的生命週期最長，但 thread safety 仍要由 service 自己保證。
- 建構子注入讓依賴清楚可替換；介面切得對、副作用有邊界，測試才好寫。
- `IOptions<T>` 是 singleton；`IOptionsSnapshot<T>` 是 scoped，不能注入 singleton。singleton 要讀可重新載入的設定，使用 `IOptionsMonitor<T>.CurrentValue`。
- 同一個介面需要多個實作時，使用 keyed services；模組註冊方法用 `TryAdd*`，只在尚未註冊時加入實作。

## 6. 面試怎麼回答

> ASP.NET Core 內建 DI container。我會在 `Program.cs` 註冊 `IOrderService` 對應 `OrderService`，controller 透過 constructor 宣告它需要 `IOrderService`，container 就會建立依賴圖。`Singleton` 整個 application lifetime 一份、`Scoped` 每個 request scope 一份、`Transient` 每次 resolve 一份。EF Core `DbContext` 通常是 scoped；singleton 若需要 scoped service，我會注入 `IServiceScopeFactory` 建立明確 scope，也會注意 singleton 的執行緒安全。

## 7. 小練習

1. 為 `IOrderService`、`OrderService`、`IOrderRepository` 寫出三行 registration。
2. 判斷 `DbContext` 應該是 singleton、scoped 還是 transient。
3. 找出 singleton → scoped dependency 的問題，說明為什麼是 bug。
