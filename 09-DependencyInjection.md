---
title: 09 Dependency Injection
tags: [aspnet-core, di, ioc, spring]
---

# 09 Dependency Injection：從 Spring DI 到 ASP.NET Core

## 學習目標

- 用 Spring `@Service` / `@Autowired` 經驗理解 ASP.NET Core built-in DI。
- 分辨 constructor injection、IoC container 與 service lifetime。
- 知道 Singleton / Scoped / Transient 的實際風險。

## 1. 一句話理解

ASP.NET Core DI container 依照你在 `Program.cs` 設定的 contract 與 lifetime 建立物件，並把 constructor 需要的 dependencies 自動注入，讓 class 不必自己 `new` 它的 collaborators。

## 2. Java 對照

Java Spring：

```java
@Service
public class UserService {
    private final UserRepository repository;

    @Autowired
    public UserService(UserRepository repository) {
        this.repository = repository;
    }
}
```

ASP.NET Core：

```csharp
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IUserRepository, UserRepository>();

public sealed class UserController : ControllerBase
{
    private readonly IUserService _userService;

    public UserController(IUserService userService)
        => _userService = userService;
}
```

Java annotation 常放在 implementation class；ASP.NET Core 常把 registration 集中在 `Program.cs` 或 extension method。兩者都是 IoC：object construction / wiring 的控制權交給 container。

## 3. C# 語法

### registration

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddTransient<EmailFormatter>();
```

### constructor injection

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class UserService(
    IUserRepository repository,
    IClock clock) : IUserService
{
    public Task<UserDto?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        // primary constructor parameters 可在 class member 使用。
        return repository.FindDtoAsync(id, clock.UtcNow, cancellationToken);
    }
}
```

上面使用 primary constructor（C# 12）。為了讓 Java 開發者先看懂，也可以寫成傳統形式：

```csharp
public sealed class UserService : IUserService
{
    private readonly IUserRepository _repository;
    private readonly IClock _clock;

    public UserService(IUserRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }
}
```

### 三種 lifetime

| Registration | 意義 | 常見用途 | 風險 |
| --- | --- | --- | --- |
| `AddSingleton` | container lifetime 只有一個 instance | stateless shared service、immutable cache、clock | 不能直接依賴 scoped；thread safety 要自己保證 |
| `AddScoped` | 每個 HTTP request 一個 scope 內一份 | `DbContext`、application service、unit of work | 不要把它 capture 到 singleton |
| `AddTransient` | 每次 resolve 產生新 instance | lightweight stateless formatter、短生命 helper | 依賴太多時會頻繁建立；disposable transient 由 container 管理時要理解 ownership |

ASP.NET Core 的 request scope 通常是一個 HTTP request；EF Core `DbContext` 常註冊 scoped。不要讓 singleton service constructor 注入 scoped service，這是典型 captive dependency。

## 4. 實務範例：controller → service → repository

```csharp
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IUserRepository, EfUserRepository>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

[ApiController]
[Route("api/users")]
public sealed class UsersController(IUserService service)
    : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDto>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
```

為什麼常見 interface + implementation + constructor injection？

- interface 讓上層依賴 contract，實作可替換。
- constructor 清楚列出 dependencies，物件建立完成時即 valid。
- test 可以注入 fake / mock，不必碰真 DB。
- lifetime 由 composition root 統一決定，不散落在 business class。

但 interface 不是宗教：只有當有替換邊界、外部資源、測試隔離或 architecture contract 時才值得建立；為每個只有一個 trivial implementation 的 class 無腦加 interface 會增加噪音。

### 把 registration 收進 extension method

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUserModule(
        this IServiceCollection services)
    {
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        return services;
    }
}

// Program.cs
builder.Services.AddUserModule();
```

## 5. 常見誤解

- DI container 不是 service locator；不要在 business code 到處注入 `IServiceProvider` 再自行 resolve。
- `Scoped` 不是「整個 application 一份」，而是每個 scope 一份；web request 通常各自有 scope。
- `Singleton` 不自動代表 thread-safe。
- constructor injection 的 interface 不會自動讓 class 好測；仍要有清楚的 contract 與 side effect boundary。
- `AddScoped<I, Impl>()` 是把 service type 與 implementation type 綁在一起，不是 Java interface 的 runtime magic。

## 6. 面試怎麼回答

> ASP.NET Core 內建 DI container。通常在 Program.cs 註冊 `IUserService` 對應 `UserService`，controller 透過 constructor 宣告它需要 `IUserService`，container 就會建立 dependency graph。`Singleton` 全 application lifetime、`Scoped` 每個 request scope 一份、`Transient` 每次 resolve 一份。EF Core `DbContext` 通常是 scoped；要避免 singleton 依賴 scoped，並注意 singleton 的 thread safety。

## 7. 小練習

1. 為 `IUserService`、`UserService`、`IUserRepository` 寫出三行 registration。
2. 判斷 `DbContext` 應該是 singleton、scoped 還是 transient。
3. 找出 singleton → scoped dependency 的問題，說明為什麼是 bug。
