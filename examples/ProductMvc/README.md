# ProductMvc sample

這是 ASP.NET Core MVC + Razor Views + EF Core CRUD 範例，使用 .NET 10 和 SQLite，因此不需要先啟動 SQL Server。

```bash
dotnet run --project examples/ProductMvc
```

開啟 `/Product` 後可以操作：

- `GET /Product`：商品清單
- `GET /Product/Details/1`：商品詳細
- `GET /Product/Create`、`POST /Product/Create`：新增
- `GET /Product/Edit/1`、`POST /Product/Edit/1`：編輯
- `GET /Product/Delete/1`、`POST /Product/Delete/1`：刪除

`ProductDbContext` 使用 `EnsureCreatedAsync()` 建立本機 SQLite 資料庫，方便範例第一次執行。正式專案應改用 migration 和部署流程；若改用 SQL Server，只需把 provider 換成 `Microsoft.EntityFrameworkCore.SqlServer`，並將 `UseSqlite` 改成 `UseSqlServer`。
