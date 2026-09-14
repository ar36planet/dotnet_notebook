# UserApi sample

這是 [[15-綜合實作|綜合實作]] 的可建置最小 ASP.NET Core Web API 範例，使用 .NET 10 SDK、內建 ASP.NET Core shared framework 與 in-memory repository，不需要 SQL Server 或額外 NuGet package。

```bash
dotnet run --project examples/UserApi/UserApi.csproj
```

這個 sample 的授權示範只接受本機測試用的 `X-Demo-User`／`X-Demo-Permission` header；它不是 production authentication。授權整合測試：

```bash
dotnet test examples/UserApi.Tests/UserApi.Tests.csproj
```

主要 endpoint：

- `GET /api/users/{id}`
- `GET /api/users/active`
- `POST /api/users`，body：`{"name":"Grace","phoneNumber":null}`

`GET /api/users/active` 另外需要 `X-Demo-Permission: users.read`。

這個 sample 的 in-memory repository 是為了讓教材容易執行；production code 通常替換成 EF Core repository，並把 `AddSingleton<IUserRepository, InMemoryUserRepository>()` 改成適合 `DbContext` scoped lifetime 的 registration。
