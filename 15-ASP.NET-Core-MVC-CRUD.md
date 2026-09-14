---
title: 15 ASP.NET Core MVC + EF Core Product CRUD
tags: [aspnet-core, mvc, razor, ef-core, crud, capstone]
---

# 15 ASP.NET Core MVC + EF Core Product CRUD

## 學習目標

- 從零讀懂一個 ASP.NET Core MVC CRUD 專案。
- 看懂 Entity、ViewModel、Repository、Service、Controller 的資料流。
- 寫出 List、Details、Create、Edit、Delete 五組 MVC actions 和 Razor Views。
- 實際使用 EF Core、model binding、`ModelState`、Anti-Forgery、TempData 和 PRG。
- 能把 SQLite sample 換成 SQL Server provider，而不改變 MVC 分層。

## 1. 一句話理解

這個範例用 `ProductController` 處理 HTML request，`ProductService` 負責 mapping 和用例，`EfProductRepository` 負責 EF Core，最後由 Razor Views 產生商品頁面。

先看資料流：

```text
GET /Product
  ↓
ProductController.Index
  ↓
ProductService.ListAsync
  ↓
EfProductRepository.ListAsync
  ↓
ProductDbContext → Products table
  ↓
Product entity → ProductViewModel
  ↓
Views/Product/Index.cshtml
  ↓
HTML
```

POST 建立商品則反過來：

```text
HTML form
  ↓
ProductCreateViewModel
  ↓ model binding + validation
ProductController.Create
  ↓
CreateProductCommand
  ↓
ProductService
  ↓
Product entity
  ↓
DbContext.SaveChangesAsync
  ↓
POST 302 → GET /Product
```

## 2. Java 對照

這個專案的概念可以對照 Spring MVC，但型別和設定 API 是 ASP.NET Core 的寫法：

| ProductMvc | Spring MVC 常見概念 |
| --- | --- |
| `ProductController : Controller` | `@Controller` |
| `ProductCreateViewModel` | form backing object |
| `[Required]`、`[Range]` | Bean Validation annotations |
| `ModelState.IsValid` | `BindingResult` + validation result |
| `return View(model)` | return view name + model |
| `RedirectToAction` | redirect view / redirect attributes |
| `TempData` | redirect 後的一次性 flash message |
| `Views/Product/Create.cshtml` | Thymeleaf template |
| `asp-for`、`asp-action` | Thymeleaf attribute |
| `DbContext` | JPA `EntityManager` 的 application abstraction |

MVC 的 ViewModel 不等於 EF Core Entity。表單只應暴露它可以修改的欄位，這是兩個 framework 都需要維持的邊界。

## 3. C# 語法

### 專案結構

```text
examples/ProductMvc/
├── Program.cs
├── Controllers/
│   ├── HomeController.cs
│   └── ProductController.cs
├── Data/
│   └── ProductDbContext.cs
├── Domain/
│   └── Product.cs
├── Models/
│   ├── ErrorViewModel.cs
│   └── ProductViewModels.cs
├── Repositories/
│   ├── IProductRepository.cs
│   └── EfProductRepository.cs
├── Services/
│   ├── IProductService.cs
│   ├── ProductCommands.cs
│   └── ProductService.cs
└── Views/
    ├── Home/
    │   ├── Index.cshtml
    │   └── Privacy.cshtml
    ├── Product/
    │   ├── Index.cshtml
    │   ├── Details.cshtml
    │   ├── Create.cshtml
    │   ├── Edit.cshtml
    │   └── Delete.cshtml
    └── Shared/
        ├── _Layout.cshtml
        ├── Error.cshtml
        └── _ValidationScriptsPartial.cshtml
```

### `Program.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProductMvc.Data;
using ProductMvc.Repositories;
using ProductMvc.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddDbContext<ProductDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("Products")
        ?? "Data Source=products.db"));
builder.Services.AddScoped<IProductRepository, EfProductRepository>();
builder.Services.AddScoped<IProductService, ProductService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

await app.Services.InitializeDatabaseAsync();

app.Run();
```

`AddControllersWithViews()` 是 MVC View 專案的註冊；`MapControllerRoute()` 是讓 `/Product`、`/Product/Edit/1` 這類 conventional route 可被找到。`DbContext` 使用 scoped lifetime，和 HTTP request scope 對齊。

.NET 9 起 MVC 範本可改用 `MapStaticAssets()` 與 `.WithStaticAssets()`；本 sample 保留 `UseStaticFiles()`，兩者都能提供靜態檔案，但範本 API 與快取／指紋行為不同。

如果部署環境使用 SQL Server，provider 和 connection string 改成：

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.11" />
```

```csharp
builder.Services.AddDbContext<ProductDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Products")));
```

Controller、Service、Repository、ViewModel 和 Razor Views 不需要因此改成另一套架構；改變的是 EF Core provider 和資料庫部署設定。這個 repo 的 sample 使用 SQLite，讓第一次執行不需要先準備 SQL Server instance。

### Entity 與 `DbContext`

```csharp
public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
```

```csharp
public sealed class ProductDbContext(
    DbContextOptions<ProductDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);
            entity.Property(product => product.Name)
                .HasMaxLength(120)
                .IsRequired();
            entity.Property(product => product.Price)
                .HasPrecision(18, 2);
        });
    }
}
```

這個 sample 用 `EnsureCreatedAsync()` 自動建立本機 SQLite table：

```csharp
public static async Task InitializeDatabaseAsync(
    this IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
    await db.Database.EnsureCreatedAsync();

    if (await db.Products.AnyAsync())
    {
        return;
    }

    db.Products.AddRange(
        new Product
        {
            Name = "USB-C 充電器",
            Price = 890m,
            CreatedAt = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero)
        },
        new Product
        {
            Name = "人體工學鍵盤",
            Price = 2_490m,
            CreatedAt = new DateTimeOffset(2026, 2, 3, 9, 0, 0, TimeSpan.Zero)
        });

    await db.SaveChangesAsync();
}
```

`EnsureCreatedAsync()` 只為了讓教材 sample 第一次執行就能跑。正式專案應使用 migration 和 schema deployment；不要把它當成 migration 的替代品。

### ViewModel

```csharp
public sealed class ProductViewModel
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public decimal Price { get; init; }
}

public sealed class ProductCreateViewModel
{
    [Required(ErrorMessage = "請輸入商品名稱。")]
    [StringLength(120, ErrorMessage = "商品名稱不能超過 120 個字。")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入價格。")]
    [Range(0.01, 100000, ErrorMessage = "價格必須介於 0.01 到 100,000。")]
    public decimal Price { get; set; }
}

public sealed class ProductEditViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "請輸入商品名稱。")]
    [StringLength(120, ErrorMessage = "商品名稱不能超過 120 個字。")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入價格。")]
    [Range(0.01, 100000, ErrorMessage = "價格必須介於 0.01 到 100,000。")]
    public decimal Price { get; set; }
}
```

`ProductViewModel` 是頁面輸出；`ProductCreateViewModel` 和 `ProductEditViewModel` 是不同的 form input。Edit 需要 `Id`，Create 不需要。這個差異就是不要直接 bind Entity 的理由。

### Repository

```csharp
public interface IProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        int id,
        CancellationToken cancellationToken);

    void Add(Product product);
    void Remove(Product product);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
```

```csharp
public sealed class EfProductRepository(ProductDbContext db)
    : IProductRepository
{
    public async Task<IReadOnlyList<Product>> ListAsync(
        CancellationToken cancellationToken)
        => await db.Products
            .AsNoTracking()
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);

    public Task<Product?> FindAsync(
        int id,
        CancellationToken cancellationToken)
        => db.Products.SingleOrDefaultAsync(
            product => product.Id == id,
            cancellationToken);

    public void Add(Product product) => db.Products.Add(product);

    public void Remove(Product product) => db.Products.Remove(product);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => db.SaveChangesAsync(cancellationToken);
}
```

列表使用 `AsNoTracking()`，因為這條 query 只讀取；這個範例的 Edit 和 Delete 先取得 tracking entity，方便回 404 並只修改允許的欄位，再由 `SaveChangesAsync` 寫回資料庫。EF Core 也能用 `Update`／`Remove` attach detached entity，或用 EF Core 7+ 的 `ExecuteUpdateAsync`／`ExecuteDeleteAsync` 做 set-based operation。

### Service 與 mapping

```csharp
public sealed record CreateProductCommand(string Name, decimal Price);
public sealed record EditProductCommand(int Id, string Name, decimal Price);
```

```csharp
public sealed class ProductService(
    IProductRepository repository) : IProductService
{
    public async Task<IReadOnlyList<ProductViewModel>> ListAsync(
        CancellationToken cancellationToken)
    {
        var products = await repository.ListAsync(cancellationToken);

        return products.Select(ToViewModel).ToList();
    }

    public async Task<ProductViewModel?> GetAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(id, cancellationToken);
        return product is null ? null : ToViewModel(product);
    }

    public async Task<ProductEditViewModel?> GetForEditAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(id, cancellationToken);

        return product is null
            ? null
            : new ProductEditViewModel
            {
                Id = product.Id,
                Name = product.Name,
                Price = product.Price
            };
    }

    public async Task<int> CreateAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = new Product
        {
            Name = command.Name.Trim(),
            Price = command.Price,
            CreatedAt = DateTimeOffset.UtcNow
        };

        repository.Add(product);
        await repository.SaveChangesAsync(cancellationToken);
        return product.Id;
    }

    public async Task<bool> UpdateAsync(
        EditProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(
            command.Id,
            cancellationToken);

        if (product is null)
        {
            return false;
        }

        product.Name = command.Name.Trim();
        product.Price = command.Price;
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await repository.FindAsync(id, cancellationToken);

        if (product is null)
        {
            return false;
        }

        repository.Remove(product);
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ProductViewModel ToViewModel(Product product)
        => new()
        {
            Id = product.Id,
            Name = product.Name,
            Price = product.Price
        };
}
```

Service 不接受瀏覽器的 `Product` entity，而是接受 command。Controller 負責把 MVC ViewModel 轉成 command；Service 負責 entity 建立、修改、刪除和 mapping。

目前 Edit 是 last-write-wins：兩個使用者讀到同一筆商品時，後送出的修改會覆蓋先送出的修改。要防止這件事，加入 concurrency token，並在 `SaveChangesAsync` 捕捉 `DbUpdateConcurrencyException`；SQLite 沒有 SQL Server `rowversion`，要改用應用程式管理的 `Version` 欄位，每次成功更新時遞增。

### Controller 全部 CRUD actions

```csharp
public sealed class ProductController(IProductService service)
    : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
        => View(await service.ListAsync(cancellationToken));

    [HttpGet]
    public async Task<IActionResult> Details(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var product = await service.GetAsync(id, cancellationToken);
        return product is null ? NotFound() : View(product);
    }

    [HttpGet]
    public IActionResult Create() => View(new ProductCreateViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        ProductCreateViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await service.CreateAsync(
            new CreateProductCommand(model.Name, model.Price),
            cancellationToken);

        TempData["Message"] = "商品已建立。";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var model = await service.GetForEditAsync(id, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        [FromRoute] int id,
        ProductEditViewModel model,
        CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var updated = await service.UpdateAsync(
            new EditProductCommand(model.Id, model.Name, model.Price),
            cancellationToken);

        if (!updated)
        {
            return NotFound();
        }

        TempData["Message"] = "商品已更新。";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Delete(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var product = await service.GetAsync(id, cancellationToken);
        return product is null ? NotFound() : View(product);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(
        [FromRoute] int id,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, cancellationToken);

        if (!deleted)
        {
            return NotFound();
        }

        TempData["Message"] = "商品已刪除。";
        return RedirectToAction(nameof(Index));
    }
}
```

GET 與 POST 的 `Delete` 參數簽章相同，C# 不允許同名同簽章的 overload，所以 POST 版本改名 `DeleteConfirmed`；`[ActionName("Delete")]` 讓 `/Product/Delete/1` 的 POST 仍對到這個 method。

## 4. 實務範例：完整 Razor Views

### `_ViewImports.cshtml` 與 `_ViewStart.cshtml`

```cshtml
@using ProductMvc
@using ProductMvc.Models
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

```cshtml
@{
    Layout = "_Layout";
}
```

`@addTagHelper` 讓 `asp-for`、`asp-action`、`asp-validation-for` 等 attribute 可以在這個資料夾底下的 views 使用。

### `Views/Shared/_Layout.cshtml`

```cshtml
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] - Product MVC</title>
    <link rel="stylesheet" href="~/lib/bootstrap/dist/css/bootstrap.min.css" />
    <link rel="stylesheet" href="~/css/site.css" asp-append-version="true" />
</head>
<body>
    <header>
        <nav class="navbar navbar-expand-sm navbar-light bg-white border-bottom mb-3">
            <div class="container-fluid">
                <a class="navbar-brand" asp-controller="Home" asp-action="Index">Product MVC</a>
                <div class="navbar-collapse">
                    <ul class="navbar-nav">
                        <li class="nav-item">
                            <a class="nav-link" asp-controller="Home" asp-action="Index">首頁</a>
                        </li>
                        <li class="nav-item">
                            <a class="nav-link" asp-controller="Product" asp-action="Index">商品</a>
                        </li>
                    </ul>
                </div>
            </div>
        </nav>
    </header>

    <div class="container">
        <main class="pb-3">
            @if (TempData["Message"] is string message)
            {
                <div class="alert alert-success" role="alert">@message</div>
            }

            @RenderBody()
        </main>
    </div>

    <footer class="border-top footer text-muted">
        <div class="container">&copy; 2026 - Product MVC</div>
    </footer>

    <script src="~/lib/jquery/dist/jquery.min.js"></script>
    <script src="~/lib/bootstrap/dist/js/bootstrap.bundle.min.js"></script>
    @await RenderSectionAsync("Scripts", required: false)
</body>
</html>
```

`@RenderBody()` 放每個 page 的內容；`TempData` 在 redirect 後被 layout 讀出；`RenderSectionAsync` 讓只有需要 validation script 的 view 加入額外 script。

### `Views/Shared/_ValidationScriptsPartial.cshtml`

```cshtml
<script src="~/lib/jquery-validation/dist/jquery.validate.min.js"></script>
<script src="~/lib/jquery-validation-unobtrusive/dist/jquery.validate.unobtrusive.min.js"></script>
```

### `Views/Product/Index.cshtml`

```cshtml
@model IReadOnlyList<ProductViewModel>
@{
    ViewData["Title"] = "商品清單";
}

<div class="d-flex justify-content-between align-items-center mb-3">
    <h1>商品清單</h1>
    <a class="btn btn-primary" asp-action="Create">新增商品</a>
</div>

@if (Model.Count == 0)
{
    <p>目前沒有商品。</p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>商品</th>
                <th>價格</th>
                <th></th>
            </tr>
        </thead>
        <tbody>
        @foreach (var product in Model)
        {
            <tr>
                <td>@product.Name</td>
                <td>NT$@product.Price.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-TW"))</td>
                <td>
                    <a asp-action="Details" asp-route-id="@product.Id">詳細</a> |
                    <a asp-action="Edit" asp-route-id="@product.Id">編輯</a> |
                    <a asp-action="Delete" asp-route-id="@product.Id">刪除</a>
                </td>
            </tr>
        }
        </tbody>
    </table>
}
```

### `Views/Product/Details.cshtml`

```cshtml
@model ProductViewModel
@{
    ViewData["Title"] = "商品詳細";
}

<h1>商品詳細</h1>
<dl class="row">
    <dt class="col-sm-2">名稱</dt>
    <dd class="col-sm-10">@Model.Name</dd>
    <dt class="col-sm-2">價格</dt>
    <dd class="col-sm-10">NT$@Model.Price.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("zh-TW"))</dd>
</dl>
<a asp-action="Edit" asp-route-id="@Model.Id">編輯</a> |
<a asp-action="Index">回到清單</a>
```

### `Views/Product/Create.cshtml`

```cshtml
@model ProductCreateViewModel
@{
    ViewData["Title"] = "新增商品";
}

<h1>新增商品</h1>
<form asp-action="Create" method="post">
    <div asp-validation-summary="ModelOnly" class="text-danger"></div>

    <div class="mb-3">
        <label asp-for="Name" class="form-label"></label>
        <input asp-for="Name" class="form-control" />
        <span asp-validation-for="Name" class="text-danger"></span>
    </div>

    <div class="mb-3">
        <label asp-for="Price" class="form-label"></label>
        <input asp-for="Price" class="form-control" />
        <span asp-validation-for="Price" class="text-danger"></span>
    </div>

    <button type="submit" class="btn btn-primary">儲存</button>
    <a asp-action="Index" class="btn btn-secondary">取消</a>
</form>

@section Scripts {
    <partial name="_ValidationScriptsPartial" />
}
```

### `Views/Product/Edit.cshtml`

```cshtml
@model ProductEditViewModel
@{
    ViewData["Title"] = "編輯商品";
}

<h1>編輯商品</h1>
<form asp-action="Edit" asp-route-id="@Model.Id" method="post">
    <input asp-for="Id" type="hidden" />
    <div asp-validation-summary="ModelOnly" class="text-danger"></div>

    <div class="mb-3">
        <label asp-for="Name" class="form-label"></label>
        <input asp-for="Name" class="form-control" />
        <span asp-validation-for="Name" class="text-danger"></span>
    </div>

    <div class="mb-3">
        <label asp-for="Price" class="form-label"></label>
        <input asp-for="Price" class="form-control" />
        <span asp-validation-for="Price" class="text-danger"></span>
    </div>

    <button type="submit" class="btn btn-primary">儲存</button>
    <a asp-action="Index" class="btn btn-secondary">取消</a>
</form>

@section Scripts {
    <partial name="_ValidationScriptsPartial" />
}
```

`asp-route-id` 產生 URL 的 route id；hidden `Id` 讓 model binder 填入 edit model。Controller 仍會比較 route id 和 model id，避免使用者竄改兩者造成更新錯誤資料。

### `Views/Product/Delete.cshtml`

```cshtml
@model ProductViewModel
@{
    ViewData["Title"] = "刪除商品";
}

<h1>刪除商品</h1>
<p>確定要刪除「@Model.Name」嗎？</p>

<form asp-action="Delete" asp-route-id="@Model.Id" method="post">
    <button type="submit" class="btn btn-danger">確認刪除</button>
    <a asp-action="Index" class="btn btn-secondary">取消</a>
</form>
```

這個 POST form 會產生 `__RequestVerificationToken` hidden input，對應 action 加 `[ValidateAntiForgeryToken]` 驗證。刪除只在 POST 執行，GET 只顯示確認頁，因為 GET 不應改變資料狀態。

## 5. 常見誤解

- `ProductViewModel` 不是 EF Core entity。它沒有 `DbContext` tracking，也不應直接被 repository 儲存。
- `AsNoTracking()` 適合 read-only list；這個範例的 Edit / Delete 先查 tracking entity，是為了回 404 並只更新允許欄位；EF Core 也支援 `Update`、`ExecuteUpdateAsync` 和 `ExecuteDeleteAsync`。
- `ModelState.IsValid` 通過只代表 binding 和 annotation validation 通過；商品是否重複、是否可下架等 domain rule 仍需 service 檢查。
- `Add` 只把 entity 放進 change tracker；真正的資料庫 I/O 在 `SaveChangesAsync`。只有 HiLo 等 value generator 情境才需要 `AddAsync`。
- `return View(model)` 會保留錯誤欄位和使用者輸入；`RedirectToAction` 適合成功後，不適合 validation error。
- `TempData` 適合「商品已建立」這種小訊息，不適合傳整個查詢結果。
- Edit／Delete 遇到併發更新或刪除時，`SaveChangesAsync` 可能丟 `DbUpdateConcurrencyException`；沒有 concurrency token 的目前 sample 是 last-write-wins。
- Delete 的 GET action 只顯示確認頁；真正刪除使用 POST，因為 GET 不應改變資料狀態。
- `ProductCreateViewModel.Price` 是非 nullable value type，會隱含 `[Required]`；若要自訂清空欄位的訊息，明確加入 `[Required(ErrorMessage = "請輸入價格。")]`。
- SQLite 的 SQL log 不能直接當成 SQL Server SQL；要換 provider 才能驗證 SQL Server 的 type、index 和 execution plan。

## 6. 面試怎麼回答

> 我會把 MVC CRUD 分成四層：Controller 處理 route、model binding、validation 和 action result；Service 處理 use case 與 Entity / ViewModel mapping；Repository 封裝 EF Core query 和 `SaveChangesAsync`；Razor View 只負責 server-side HTML。Create / Edit 使用專用 ViewModel，避免直接 bind Entity 造成 overposting。POST 成功後用 `RedirectToAction` 套用 PRG，表單 action 加 `[ValidateAntiForgeryToken]`，讀取清單則用 `AsNoTracking` 和 `ToListAsync`。

## 7. 小練習

1. 把 Product sample 的 `UseSqlite` 改成 `UseSqlServer`，列出需要的 package 和 connection string。
2. 讓清單支援 `?keyword=keyboard`，並決定 keyword 要放 query string 還是 form。
3. 加入 `ProductEditViewModel` 的 optimistic concurrency 欄位，說明需要修改哪些 entity、view 和 service。SQLite 沒有自動更新的 `rowversion`；可用 `Version` 整數欄位，以 `IsConcurrencyToken()` 設定並在更新時遞增。
4. 讓 `Create` 在商品名稱已存在時加入 `ModelState.AddModelError`，並重新 render `Create.cshtml`。
5. 寫一個同時提供 `GET /Product` HTML 和 `GET /api/products` JSON 的專案，列出哪些類別可以共用、哪些 response model 不該共用。
