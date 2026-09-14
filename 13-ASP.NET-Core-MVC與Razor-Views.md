---
title: 13 ASP.NET Core MVC 與 Razor Views
tags: [aspnet-core, mvc, razor, model-binding, validation, ef-core]
---

# 13 ASP.NET Core MVC 與 Razor Views

## 學習目標

- 看懂 MVC 中 Model、View、Controller 的責任邊界。
- 從瀏覽器 request 追到 controller、service、EF Core、Razor View 和 HTML response。
- 能使用 conventional routing、attribute routing、model binding 和 `ModelState`。
- 能寫出包含 Layout、Partial View、Tag Helpers、表單驗證與 PRG 的 CRUD 頁面。
- 分辨 MVC controller 和 Web API `ControllerBase` 的回應模型。
- 看懂企業專案中的 Entity、ViewModel、DTO、TempData、Session 和 Anti-Forgery。

## 1. 一句話理解

ASP.NET Core MVC 把瀏覽器 request 交給 Controller，Controller 呼叫 service 取得或修改 Model，再把 ViewModel 傳給 Razor View 產生 HTML；Web API 則通常把同一份資料轉成 JSON。

先看一個會出事的場景：表單 POST 成功後直接 `return View("Index")`。瀏覽器此時仍停在 POST request；使用者重新整理頁面，瀏覽器會再次送出同一份表單，商品可能被建立兩次。MVC 的基本流程不是只有「action 呼叫資料庫」，而是要看清楚 response 的種類：

```text
Browser
  ↓ HTTP request
Routing
  ↓
Controller action
  ↓
Service
  ↓
Repository / EF Core
  ↓
Entity
  ↓ mapping
ViewModel
  ↓
Razor View
  ↓ HTML response
Browser
```

## 2. Java 對照

如果讀過 Spring MVC，可以用下面的對照快速定位概念。名稱相近不代表 lifecycle 和設定方式完全相同。

| ASP.NET Core MVC | Spring MVC 常見對照 | 重點 |
| --- | --- | --- |
| `Controller` | `@Controller` | 收 request、呼叫 application service、選擇 view 或 redirect |
| `ViewModel` | form object / view model | 只放某個頁面需要的輸入或輸出欄位 |
| Razor View `.cshtml` | Thymeleaf template | server-side template，最後產生 HTML |
| Model binding | `@ModelAttribute` / argument resolver | 把 route、query、form 轉成 C# 參數或物件 |
| `ModelState` | `BindingResult` | 保存 binding 與 validation 結果 |
| Data Annotations | Bean Validation annotations | `[Required]`、`[Range]` 對應常見欄位規則 |
| `Tag Helpers` | Thymeleaf attributes | 在 HTML 屬性上加入 server-side URL、form、validation 行為 |
| `TempData` | `RedirectAttributes.addFlashAttribute` | 暫存跨 redirect 要顯示的一次性訊息 |
| `Partial View` | fragment | 重用一段沒有獨立 request flow 的 HTML |
| `View Component` | 沒有直接對應；最接近是 template fragment 搭配 server-side service | 自己執行 server-side code 再產生一段 HTML |

ASP.NET Core MVC 使用 `Program.cs` 組合 host、DI、middleware 和 endpoint；不需要 `Startup.cs` 才能建立 MVC app。現代專案通常會把 controller 保持在 UI 邊界，把商業規則放進 service。

## 3. C# 語法

### MVC 三個角色

以商品管理為例：

```csharp
// Model：資料狀態與 domain 規則的其中一部分
public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
}
```

- Model 表示 application 的資料與行為。這個詞在不同專案可能泛指 domain entity、資料存取模型或頁面 ViewModel，讀 code 時要看實際型別名稱。
- View 負責呈現資料和收集使用者輸入，不應在 `.cshtml` 裡直接查資料庫。
- Controller 負責 HTTP 邊界：接收參數、檢查 `ModelState`、呼叫 service、選擇 `View`、redirect 或 status code。

### 一個 request 如何變成 HTML

假設瀏覽器送出：

```text
GET /Product/Details/10
```

處理順序如下：

1. Kestrel 收到 HTTP request，middleware 執行 logging、exception handling、static files、routing 等工作。
2. Routing 根據 URL 和 HTTP method 找到 `ProductController.Details(int id)`。
3. Model binding 把 route value `10` 轉成 `int id`。
4. Controller 透過 DI 取得 `IProductService`，service 再查 repository / EF Core。
5. Controller 把查詢結果映射成 `ProductViewModel`。
6. `return View(model)` 建立 `ViewResult`；MVC 依 action 和 controller 找 `Views/Product/Details.cshtml`。
7. Razor 執行 `.cshtml`，把 `@Model.Name` 插入 HTML，並做 HTML encoding。
8. MVC 把產生的 HTML 寫進 HTTP response，瀏覽器解析並顯示頁面。

```csharp
public sealed class ProductController(IProductService service)
    : Controller
{
    [HttpGet]
    public async Task<IActionResult> Details(
        int id,
        CancellationToken cancellationToken)
    {
        var product = await service.GetAsync(id, cancellationToken);

        return product is null
            ? NotFound()
            : View(product);
    }
}
```

`return View(product)` 的 `product` 會成為 Razor View 的 `ViewData.Model`，因此 view 可以用 `@Model.Name` 讀取它。`return View()` 則不傳 model，view 的 `Model` 會是 `null`，除非 action 事先透過其他方式準備資料。

### `View`、`Ok`、`RedirectToAction` 的差別

```csharp
return View(model);
```

- 建立 `ViewResult`。
- MVC 找 `.cshtml`，執行 Razor，回傳通常是 `200 text/html`。
- 適合瀏覽器要取得一個頁面，或表單驗證失敗後把錯誤重新顯示在原頁面。

```csharp
return Ok(model);
```

- 建立 `OkObjectResult`，狀態碼是 `200`。
- 由 output formatter 將物件序列化成 JSON 或其他格式。
- 適合 API client；它不會去找 `Views/Product/Index.cshtml`。

```csharp
return RedirectToAction(nameof(Index));
```

- 建立 redirect result，通常是 `302 Found`，並在 `Location` header 放入下一個 URL。
- 瀏覽器收到後再發一個 `GET /Product`。
- 適合 POST 成功後套用 PRG，避免重新整理重送表單。

### 現代 `Program.cs`

MVC 需要 `AddControllersWithViews()` 和 conventional route：

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
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

app.Run();
```

`AddControllers()` 只註冊 controller 相關服務，適合不需要 Razor View 的 API。`AddControllersWithViews()` 另外啟用 MVC views、Razor view engine 和相關功能。少了 `MapControllerRoute()`，conventional-routed MVC action 沒有被接到 endpoint。

.NET 9 起 MVC 範本改用 `app.MapStaticAssets()`（支援壓縮與指紋）；`UseStaticFiles()` 仍可使用，兩者不是同一個 API。

### Conventional Routing

```csharp
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
```

這個 pattern 的每一段意思是：

| URL | controller | action | `id` |
| --- | --- | --- | --- |
| `/Home/Index` | `HomeController` | `Index` | 沒有 |
| `/Product/Details/10` | `ProductController` | `Details` | `10` |
| `/Product` | `ProductController` | 預設 `Index` | 沒有 |
| `/` | 預設 `HomeController` | 預設 `Index` | 沒有 |

對應的 controller 可以是：

```csharp
public class ProductController : Controller
{
    public IActionResult Details(int id)
    {
        // id 由 route data 經 model binding 取得
        return View(id);
    }
}
```

`{id?}` 的 `?` 代表 optional。缺少值時，非 nullable `int id` 會得到 `0`，`ModelState.IsValid` 仍然是 `true`；要把缺值視為錯誤，可使用 `int?` 自己判斷、加 `[BindRequired]`，或用 route constraint 排除不合法 URL。

實際結果：

```text
GET /Product/Details → id=0, ModelState.IsValid=True
```

### Attribute Routing

Attribute routing 把 route 靠近 controller 和 action。它不只用在 API，MVC 也可以用它產生 HTML 頁面：

```csharp
[Route("products")]
public class ProductController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("{id:int}")]
    public IActionResult Details(int id) => View(id);

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public IActionResult Create(ProductCreateViewModel model)
        => !ModelState.IsValid ? View(model) : RedirectToAction(nameof(Index));
}
```

已經有 `MapControllerRoute()` 的 MVC 專案不必再加 `MapControllers()`；`MapControllerRoute()` 同時對應 conventional 與 attribute-routed controller。只有純 attribute routing（例如純 API 專案）才使用 `MapControllers()`：

```csharp
app.MapControllers();
```

conventional routing 和 attribute routing 可以共存，但 controller 或 action 一旦放了 `[Route]`／`[HttpGet("...")]`，就只能由 attribute route 到達；controller 上有 route attribute 時，該 controller 的所有 action 都變成 attribute-routed。第 216 行的範例 URL 是 `/products`、`/products/10`、`/products/create`，不再是 `/Product/Details/10`。

### HTTP method attributes

```csharp
[HttpGet]     // 讀取頁面或資料
[HttpPost]    // 表單送出、建立資料
[HttpPut]     // 整體更新，API 較常見
[HttpDelete]  // 刪除，API 較常見
[Route("...")] // 定義 route template
```

`[HttpGet]`、`[HttpPost]` 不會讓 action 自動變成 API。它們只是在 routing 時限制 HTTP method。MVC 瀏覽器表單通常使用 GET 顯示頁面、POST 送出表單；HTML `<form>` 原生支援的 method 主要是 GET 和 POST，`PUT` / `DELETE` 通常留給 API，或由 JavaScript / method override 方案送出。

### Controller 與 Action 回傳型別

常見型別的使用時機：

| 型別或 helper | MVC 用途 |
| --- | --- |
| `IActionResult` | 一個 action 可能回傳 `View`、`NotFound`、`RedirectToAction` 等不同結果 |
| `ActionResult` | 實作 `IActionResult` 的抽象類別；`ViewResult`、`RedirectToActionResult`、`ObjectResult` 等具體 result 都繼承它 |
| `ActionResult<T>` | API 需要在 `T`、`NotFound`、`BadRequest` 間切換時常用，MVC view action 不必勉強使用 |
| `ViewResult` | 明確表示要 render Razor View，例如 `return View(model)` |
| `RedirectToActionResult` | `RedirectToAction` 產生的 redirect result |
| `NotFoundResult` | `404`，例如 id 不存在 |
| `BadRequestResult` | `400`，例如 route id 和 form id 不一致 |
| `JsonResult` | 明確要求 JSON；一般 API 更常使用 `ControllerBase` 的 `Ok` 與 formatter |

```csharp
public IActionResult Index() => View();

public IActionResult Details(int id)
    => id <= 0 ? BadRequest() : View(id);

public IActionResult MissingProduct() => NotFound();

public IActionResult Saved() => RedirectToAction(nameof(Index));
```

MVC controller 通常繼承 `Controller`：

```csharp
public class ProductController : Controller
{
    public IActionResult Index() => View();
}
```

`Controller` 繼承自 `ControllerBase`，再加上 `View()`、`ViewData`、`ViewBag`、`TempData`、`PartialView()` 等 view-oriented helpers。

`ActionResult<IReadOnlyList<T>>` 使用介面型別時，請透過 `Ok(list)` 回傳；C# 不支援介面型別的 implicit conversion，不能直接 `return list;`。

Web API 常見寫法則是：

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductController(IProductService service)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> Get(
        CancellationToken cancellationToken)
        => Ok(await service.ListAsync(cancellationToken));
}
```

兩者都可以使用 DI、filters、routing、model binding 和 validation；差別在 response 目標：MVC `Controller` 常把 ViewModel 交給 Razor 產生 HTML，API `ControllerBase` 常把 DTO 交給 formatter 產生 JSON。`[ApiController]` 還會帶來 API 常見的自動 model validation response，不能直接套到需要重新 render 表單的 MVC action。

### Model Binding

Model binding 把 request 中的資料轉成 action 參數或物件。以這個 URL 為例：

```text
GET /products/edit/10?tab=pricing
```

```csharp
public IActionResult Edit(
    [FromRoute] int id,
    [FromQuery] string? tab)
{
    // id = 10，tab = "pricing"
    return View();
}
```

表單的欄位名稱則要對上 ViewModel property：

```html
<form method="post">
    <input name="Name" value="USB-C 充電器" />
    <input name="Price" value="890" />
</form>
```

```csharp
[HttpPost]
public IActionResult Create(ProductCreateViewModel model)
{
    // model.Name = "USB-C 充電器"
    // model.Price = 890
    return View(model);
}
```

常見 binding source：

| Attribute | 來源 | MVC 範例 |
| --- | --- | --- |
| `[FromRoute]` | route data | `/Product/Details/10` 的 `id` |
| `[FromQuery]` | query string | `/Product?search=keyboard` 的 `search` |
| `[FromForm]` | `application/x-www-form-urlencoded` 或 multipart form | `<input name="Name">` |
| `[FromBody]` | request body，由 input formatter 解析 | JSON API request |

```csharp
public IActionResult Search([FromQuery] string? keyword)
    => View(keyword);

[HttpPost]
public IActionResult Create([FromForm] ProductCreateViewModel model)
    => View(model);

[HttpPost("api/products")]
public IActionResult CreateApi([FromBody] ProductCreateRequest request)
    => Ok(request);
```

在 MVC 表單 action 中，`ProductCreateViewModel model` 通常會從 form fields binding；`[FromBody]` 則要求 runtime 讀取整個 body，並透過 JSON 或其他 input formatter 解析。單一 action 不要放兩個 `[FromBody]` 參數，因為 request body 通常只能被讀取一次。

MVC 與 Web API 的常見差異如下：

- MVC views 主要接收 form、route、query；驗證失敗時由 action `return View(model)` 把 `ModelState` 帶回頁面。
- 標記 `[ApiController]` 的 API 會對 complex parameter 套用 API 導向的 binding convention，validation 失敗時通常直接回 `400`，不會 render `.cshtml`。
- API JSON body 通常明確寫 `[FromBody]` 或依 `[ApiController]` convention binding；MVC form 則使用 `application/x-www-form-urlencoded`。

### Razor Views

Razor View 是 `.cshtml` 檔案，HTML 是主要內容，`@` 讓 Razor 進入 C#：

```cshtml
@model ProductViewModel

<h1>@Model.Name</h1>
<p>@Model.Price.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("zh-TW"))</p>
```

條件：

```cshtml
@if (Model.Price >= 1000)
{
    <span>高單價商品</span>
}
```

清單 view 使用集合型別：

```cshtml
@model IReadOnlyList<ProductViewModel>

@foreach (var product in Model)
{
    <li>@product.Name</li>
}
```

`@model` 宣告這個 view 的強型別 model；`@Model` 讀取 controller 傳入的物件。強型別 ViewModel 讓 IDE 提供 IntelliSense，也讓 Razor compile 時較早發現 property 名稱錯誤。

Razor 預設會對插入 HTML 的字串做 encoding：

```cshtml
@Model.Name
```

如果 `Name` 是 `<script>alert(1)</script>`，它會被當成文字輸出，而不是直接執行。不要為了「讓 HTML 顯示出來」就任意使用 `@Html.Raw`；只有確定內容已經過可信任的 HTML sanitization，才有理由略過 encoding。

### Layout、Partial View、View Component、Section

常見 MVC view 目錄：

```text
Views/
├── _ViewImports.cshtml
├── _ViewStart.cshtml
├── Shared/
│   ├── _Layout.cshtml
│   └── _ValidationScriptsPartial.cshtml
├── Product/
│   ├── Index.cshtml
│   ├── Details.cshtml
│   ├── Create.cshtml
│   └── Edit.cshtml
└── Home/
    └── Index.cshtml
```

`Views/_ViewImports.cshtml` 會套用到該目錄下的 views，常見內容是：

```cshtml
@using ProductMvc.Models
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

沒有 `@addTagHelper` 時，`asp-for`、`asp-action`、`<partial>` 等標記不會被 Tag Helper 處理。

`_ViewStart.cshtml` 可以設定所有 view 的預設 layout：

```cshtml
@{
    Layout = "_Layout";
}
```

`_Layout.cshtml` 放所有頁面共用的 HTML，例如 `<head>`、導覽列、footer：

```cshtml
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
    <meta charset="utf-8" />
    <title>@ViewData["Title"] - 商品管理</title>
</head>
<body>
    <nav>
        <a asp-controller="Home" asp-action="Index">首頁</a>
        <a asp-controller="Product" asp-action="Index">商品</a>
    </nav>

    @RenderBody()
    @await RenderSectionAsync("Scripts", required: false)
</body>
</html>
```

每個 view 的內容會放進 `@RenderBody()`。需要額外 JavaScript 的頁面可以宣告 section：

```cshtml
@section Scripts {
    <partial name="_ValidationScriptsPartial" />
}
```

Partial View 是沒有獨立 action flow 的可重用 `.cshtml` 片段：

```cshtml
<partial name="_ValidationScriptsPartial" />
```

它適合共用 HTML；如果該區塊需要自己查購物車、判斷登入狀態或執行 server-side code，使用 View Component：

```csharp
public sealed class CartSummaryViewComponent(ICartService service)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
        => View(await service.GetSummaryAsync(HttpContext.RequestAborted));
}
```

```cshtml
@await Component.InvokeAsync("CartSummary")
```

View Component 的參數只來自 `Component.InvokeAsync` 傳入的匿名物件，不經過 model binding，也不會自動取得 request 的 `CancellationToken`；需要取消時直接使用 `HttpContext.RequestAborted`。

Partial View 主要是 markup reuse；View Component 有自己的 class、參數和 server-side logic。兩者都不是拿來取代 Layout 的。

### Tag Helpers 與 HTML Helpers

Tag Helpers 讓 server-side code 參與既有 HTML element 的產生。以下 Razor：

```cshtml
<a asp-controller="Product"
   asp-action="Details"
   asp-route-id="@item.Id">
    詳細
</a>
```

在 default conventional route 和 `item.Id == 10` 時，輸出會接近：

```html
<a href="/Product/Details/10">詳細</a>
```

```cshtml
<form asp-action="Create" method="post">
```

會產生 action URL，並且在 MVC form 的 anti-forgery 設定下於 `</form>` 前加入 hidden input。token 每次不同，實際輸出可看到 `CfDJ8...` 開頭的 Data Protection 字串：

```html
<form action="/Product/Create" method="post">
    <input name="__RequestVerificationToken" type="hidden"
           value="CfDJ8DA8YyZjWb1N...（每次 request 不同，這裡截斷）" />
</form>
```

```cshtml
<input asp-for="Name" />
<span asp-validation-for="Name"></span>
```

假設 `Name` 有 `[Required(ErrorMessage = "請輸入商品名稱。")]`，實際輸出包含 `id`、`name`、空的 `value` 和 client-side validation metadata：

```html
<input type="text" data-val="true" data-val-required="&#x8ACB;&#x8F38;&#x5165;&#x5546;&#x54C1;&#x540D;&#x7A31;&#x3002;" id="Name" name="Name" value="" />
<span class="field-validation-valid" data-valmsg-for="Name" data-valmsg-replace="true"></span>
```

這段 markup 不是瀏覽器認得的 `asp-for`；Tag Helper 在 server render 階段先把它轉成一般 HTML。`asp-route-id` 也不是字串拼接：它會交給 URL generation，route pattern 變更時，view 不必手動修改 `/Product/Details/10`。

同一件事可以用 HTML Helper 寫：

```cshtml
@using (Html.BeginForm("Create", "Product", FormMethod.Post))
{
    @Html.AntiForgeryToken()
    @Html.LabelFor(model => model.Name)
    @Html.EditorFor(model => model.Name)
    @Html.ValidationMessageFor(model => model.Name)
}
```

現代 MVC 專案通常優先用 Tag Helpers，因為它保留一般 HTML 結構、URL 和 validation metadata 也較容易從畫面直接讀懂；既有企業專案仍可能大量使用 HTML Helpers。

### Entity、ViewModel、DTO

不要把 EF Core Entity 直接當成表單 model：

```csharp
public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public bool IsDiscontinued { get; set; }
}
```

建立商品的畫面其實不應該讓使用者提交 `Id` 或 `IsDiscontinued`：

```csharp
public sealed class ProductCreateViewModel
{
    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 100000)]
    public decimal Price { get; set; }
}
```

三者責任可以畫成兩條方向：

```text
讀取：Database Entity → Service mapping → ViewModel → Razor View → HTML

寫入：HTML form → Model Binding → ViewModel → Service mapping → Entity → EF Core → Database
```

- Entity 對應 persistence、identity、tracking 和資料庫欄位。
- ViewModel 只對應某個頁面需要顯示或提交的欄位；Create 和 Edit 常應該是不同型別。
- DTO 是跨 HTTP API 邊界的資料 contract。MVC 頁面也可以使用 DTO，但頁面表單通常會用更貼近畫面的 ViewModel。

直接 bind Entity 會增加 overposting 風險：攻擊者手動多送 `IsDiscontinued=true` 或修改不該由這個頁面控制的欄位，model binder 可能照欄位名稱填入 entity。使用專用 ViewModel 讓可寫欄位本身成為明確的 allow-list。

### Validation 與 `ModelState`

Data Annotations 可以提供基本欄位規則：

```csharp
public sealed class ProductCreateViewModel
{
    [Required(ErrorMessage = "請輸入商品名稱。")]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 100000)]
    public decimal Price { get; set; }

    [EmailAddress]
    public string? ContactEmail { get; set; }

    [Compare(nameof(ConfirmEmail))]
    public string? Email { get; set; }

    public string? ConfirmEmail { get; set; }
}
```

Model binding 先把 request 值轉成 property，再執行 validation。binding 轉換失敗，例如把 `abc` 填入 `decimal Price`，也會把錯誤放進 `ModelState`。action 要明確檢查：

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Create(
    ProductCreateViewModel model,
    CancellationToken cancellationToken)
{
    if (!ModelState.IsValid)
    {
        // 保留 model 和 ModelState，讓原頁面顯示使用者輸入與錯誤
        return View(model);
    }

    await service.CreateAsync(
        new CreateProductCommand(model.Name, model.Price),
        cancellationToken);
    return RedirectToAction(nameof(Index));
}
```

View 以 Validation Tag Helpers 顯示錯誤：

```cshtml
<div asp-validation-summary="ModelOnly" class="text-danger"></div>

<label asp-for="Name"></label>
<input asp-for="Name" />
<span asp-validation-for="Name" class="text-danger"></span>
```

client-side validation 可以讓錯誤更早出現在瀏覽器，但不能取代 server-side validation。request 可以關掉 JavaScript，也可以直接繞過瀏覽器送 HTTP；server 端仍要檢查 `ModelState` 和 domain rule。

### Form POST 與 PRG

成功的表單 action 應使用 Post → Redirect → Get：

```csharp
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
```

流程是：

```text
POST /Product/Create
    ↓ 302 Location: /Product
GET /Product
    ↓ 200 HTML
```

`return View("Index")` 只是用同一個 POST request render view；它沒有把瀏覽器狀態改成 GET。重新整理時，瀏覽器會詢問是否要重送 POST body。PRG 讓成功的 mutation 只執行一次，重新整理只會重新讀取清單。

驗證失敗時不要 redirect，因為 `ModelState` 和原始輸入值需要直接帶回同一個 request 的 view；redirect 後這些資料不會自動保留。成功後才使用 `TempData` 傳一次性的提示。

### Anti-Forgery 與 CSRF

MVC form 常靠 cookie 驗證使用者。瀏覽器會自動把 cookie 附在 request 上，攻擊者可以利用這件事：

```text
1. 使用者登入 shop.example，瀏覽器保存登入 cookie。
2. 使用者在另一個分頁開啟 evil.example。
3. evil.example 自動 POST 到 shop.example/Product/Delete/10。
4. 瀏覽器自動附上 shop.example cookie。
5. 如果 server 只信 cookie，商品可能被刪除。
```

Anti-Forgery Token 使用 synchronizer token pattern：server render form 時放一個不可預測的 hidden request token，POST 時比對它與 cookie 中的 cookie token。這是無狀態配對，不需要 server-side session data。

```cshtml
<form asp-action="Create" method="post">
    <input asp-for="Name" />
    <button type="submit">儲存</button>
</form>
```

Form Tag Helper 會協助產生 token；action 再明確要求驗證：

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult Create(ProductCreateViewModel model)
{
    // 沒有合法 token 時，action 不會照正常流程執行
    return View(model);
}
```

MVC controller 不會像 Razor Pages 一樣自動套用 Anti-Forgery。大型 MVC app 可在全域加入 `AutoValidateAntiforgeryToken`，它只驗證 POST、PUT、PATCH、DELETE 等不安全方法，GET／HEAD／OPTIONS／TRACE 不要求 token：

```csharp
builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
```

如果攻擊者只知道 URL，卻拿不到同源頁面產生的 token，POST 就不能通過檢查。Anti-Forgery 不是輸入驗證，也不是 authorization；它解決的是「瀏覽器自動帶 cookie 的跨站請求」問題。使用 bearer token 且 API 不使用 cookie 驗證時，CSRF 風險模型不同，但仍要依 authentication、CORS 和瀏覽器使用方式做安全判斷。

### Cookie、Session、TempData、ViewData、ViewBag

| 工具 | 生命週期 / 儲存位置 | 適合放什麼 |
| --- | --- | --- |
| `Model` | 目前 action → 目前 View | 這個頁面的主要資料，優先使用 |
| `ViewData` | 目前 request 的 dictionary | 少量 view metadata，例如 title；key 是字串 |
| `ViewBag` | `ViewData` 的 dynamic wrapper | 舊專案常見；沒有 compile-time property 檢查 |
| `TempData` | 預設放在加密 cookie，保留到下一個 request；讀取後消費，可用 `Peek`／`Keep` 保留 | POST redirect 後顯示一次性的成功或錯誤訊息；內容應小於約 4 KB |
| Cookie | 瀏覽器保存、每次符合條件的 request 自動送回 | 小型 client preference、非敏感識別資料；不要直接放秘密 |
| Session | server-side / distributed store，cookie 通常只帶 session id | 跨 request 的暫時購物車或 wizard state |

```csharp
ViewData["Title"] = "商品清單";
ViewBag.Title = "商品清單"; // 等價地寫入 ViewData
TempData["Message"] = "Saved successfully";
```

```cshtml
<title>@ViewData["Title"]</title>
<h1>@ViewBag.Title</h1>
```

`TempData` 的使用方式：

```csharp
TempData["Message"] = "商品已儲存。";
return RedirectToAction(nameof(Index));
```

下一個 request 的 layout 可以讀到 `TempData["Message"]`，顯示後該值通常就會被標記為已讀。TempData 只適合小型訊息，不要拿來塞整個商品清單。Session 需要註冊 distributed cache 和 `UseSession()`：

```csharp
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession();

var app = builder.Build();
app.UseRouting();
app.UseSession();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
```

`UseSession()` 要放在需要讀取 session 的 middleware / endpoint 之前；正式多機部署要考慮 distributed store，而不是把 session 只放在單機記憶體。

### MVC + EF Core CRUD 的分層

可執行的完整範例在 `examples/ProductMvc`；對應教材說明在 [[15-ASP.NET-Core-MVC-CRUD]]。它使用 SQLite 讓讀者不必先啟動 SQL Server，但 Controller、Service、Repository、`DbContext`、ViewModel 和 Razor Views 的分工與 SQL Server provider 相同。

```text
examples/ProductMvc/
├── Controllers/ProductController.cs
├── Data/ProductDbContext.cs
├── Domain/Product.cs
├── Models/ProductViewModels.cs
├── Repositories/
│   ├── IProductRepository.cs
│   └── EfProductRepository.cs
├── Services/
│   ├── IProductService.cs
│   ├── ProductCommands.cs
│   └── ProductService.cs
└── Views/
    ├── Product/
    │   ├── Index.cshtml
    │   ├── Details.cshtml
    │   ├── Create.cshtml
    │   ├── Edit.cshtml
    │   └── Delete.cshtml
    └── Shared/
        ├── _Layout.cshtml
        └── _ValidationScriptsPartial.cshtml
```

讀取清單：

```csharp
// Repository：把 EF Core query materialize 成 entity snapshot
public async Task<IReadOnlyList<Product>> ListAsync(
    CancellationToken cancellationToken)
    => await db.Products
        .AsNoTracking()
        .OrderBy(product => product.Id)
        .ToListAsync(cancellationToken);

// Service：Entity → ViewModel
public async Task<IReadOnlyList<ProductViewModel>> ListAsync(
    CancellationToken cancellationToken)
{
    var products = await repository.ListAsync(cancellationToken);

    return products
        .Select(product => new ProductViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Price = product.Price
        })
        .ToList();
}
```

建立商品：

```text
POST form
  ↓ Model Binding + Data Annotations
ProductCreateViewModel
  ↓ Controller mapping
CreateProductCommand
  ↓ Service
Product entity
  ↓ DbContext.SaveChangesAsync
SQLite / SQL Server
```

刪除也要使用 POST form 加上 Anti-Forgery，而不是讓 GET `/Product/Delete/10` 直接刪資料。GET action 只顯示確認頁；POST action 才執行 mutation。

### MVC 與 Web API 對照

| MVC | Web API |
| --- | --- |
| `Controller` | `ControllerBase` |
| `View()` | `Ok()` / `Created()` |
| Razor View | JSON formatter |
| HTML response | JSON response |
| form fields | JSON request body |
| Page ViewModel | API DTO |
| Tag Helpers | serializer / API contract |
| Cookie、Session、TempData 常見 | bearer token、client-managed state 常見 |
| 直接服務瀏覽器頁面 | 服務 SPA、mobile、另一個 application |

同一個 Product 功能可以有兩個 endpoint：

```text
GET /Product
    → ProductController.Index
    → Product/Index.cshtml
    → 200 text/html

GET /api/products
    → ProductsApiController.Get
    → ProductResponse DTO
    → 200 application/json
```

兩個 controller 可以共用 service 和 repository，但 response boundary 不要混在一起。MVC 頁面需要 URL generation、Razor、form token 和 validation messages；API client 需要 status code、JSON schema 和 API authentication。

對應的 API controller 可以是：

```csharp
[ApiController]
[Route("api/products")]
public sealed class ProductsApiController(IProductService service)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductViewModel>>> Get(
        CancellationToken cancellationToken)
        => Ok(await service.ListAsync(cancellationToken));
}
```

因此同一個 `IProductService.ListAsync` 可以被 MVC page 和 API endpoint 使用；前者回 Razor HTML，後者回 JSON。實務上 API 通常會再使用專用 `ProductResponse` DTO，避免把頁面 ViewModel 當成跨系統 contract。

### B 級與 C 級內容

先熟悉 CRUD 主流程，再補下面的概念：

| 優先級 | 主題 | 先知道什麼 |
| --- | --- | --- |
| B | Filters | action 選定後依序經過 Authorization → Resource → model binding → Action → Exception → Result；把共用工作放在對應 filter，不要塞進 controller |
| B | Areas | 以 `Areas/Admin/Controllers`、`Areas/Admin/Views` 分隔後台等大型功能區 |
| B | Authorization | Authentication 確認你是誰；authorization 判斷你能不能做這件事，MVC 頁面常配 cookie auth 和 policy |
| B | View Component | 共用 UI 片段需要自己執行 server-side code 時使用 |
| C | 自訂 Tag Helper | 只有內建 Tag Helpers 和 partial / view component 不足時再建立 |
| C | 自訂 View Engine | 了解它是替換 view resolution / rendering 的 extension point，不是日常 MVC CRUD 技能 |
| C | 複雜 View Component | 先把服務邏輯抽到 service，避免 View Component 變成另一個 controller |

## 4. 實務範例：用 Product MVC 跑一個 CRUD

在 repo 根目錄執行：

```bash
dotnet run --project examples/ProductMvc/ProductMvc.csproj
```

開啟 `/Product` 後，範例提供：

```text
GET  /Product
GET  /Product/Details/1
GET  /Product/Create
POST /Product/Create
GET  /Product/Edit/1
POST /Product/Edit/1
GET  /Product/Delete/1
POST /Product/Delete/1
```

最重要的 controller action：

```csharp
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
```

對應的完整 `Create.cshtml`：

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

清單 view 用 `asp-route-id` 產生每一筆商品的 URL：

```cshtml
@model IReadOnlyList<ProductViewModel>

<a asp-action="Create">新增商品</a>

@foreach (var product in Model)
{
    <span>@product.Name</span>
    <span>@product.Price.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("zh-TW"))</span>
    <a asp-action="Details" asp-route-id="@product.Id">詳細</a>
    <a asp-action="Edit" asp-route-id="@product.Id">編輯</a>
    <a asp-action="Delete" asp-route-id="@product.Id">刪除</a>
}
```

這個專案的 SQLite log 實際會看到類似下面的 SQL；provider 換成 SQL Server 後，SQL 方言和參數標記會不同，但 `ToListAsync` 仍是 query execution boundary：

```sql
SELECT "p"."Id", "p"."CreatedAt", "p"."Name", "p"."Price"
FROM "Products" AS "p"
ORDER BY "p"."Id";

INSERT INTO "Products" ("CreatedAt", "Name", "Price")
VALUES (@p0, @p1, @p2)
RETURNING "Id";
```

sample 使用 `EnsureCreatedAsync()` 讓第一次執行不必先建立 migration；正式 SQL Server 專案應使用 migration、connection string secret management 和部署時的 schema migration 流程。

## 5. 常見誤解

- `Controller` 不等於 API controller。是否回 HTML 或 JSON，要看繼承類別、action result 和 endpoint 的設計。
- `return View(model)` 不會呼叫另一個 HTTP request；它在目前 request 內 render `.cshtml`。
- `return RedirectToAction` 不是把另一個 action method 直接呼叫一次；它回傳 redirect，瀏覽器通常會再發一個 GET。
- `AddControllersWithViews()` 和 `MapControllerRoute()` 是兩件事：前者註冊服務，後者把 route 接到 pipeline。
- Tag Helper 只在 server render 時存在；送到瀏覽器的 HTML 不會保留 `asp-for` 或 `asp-action`。
- `ModelState.IsValid` 不只包含 Data Annotations；型別轉換失敗也會加入 `ModelState` error。
- validation 失敗時不要 redirect，否則原始輸入和錯誤訊息不會自然跟到下一個 request。
- `[ValidateAntiForgeryToken]` 防 CSRF，不負責登入、權限、輸入驗證或 SQL injection。
- `ViewBag` 不是另一個儲存區；它只是以 dynamic 方式存取 `ViewData`。
- ViewModel 不是 Entity 的別名。Create、Edit、List、Details 可能各自有不同的欄位需求。
- GET Delete 只應顯示確認頁；真正刪除使用 POST，並檢查 Anti-Forgery。
- Partial View 沒有獨立 controller flow；需要查資料或執行 server-side code 時，考慮 View Component。
- MVC 和 Web API 可以存在同一個 ASP.NET Core 專案；共用 service / EF Core，不代表共用 response shape。
- SQLite sample 可以跑 CRUD，但 `EnsureCreatedAsync()` 不是 production migration strategy。

## 6. 面試怎麼回答

> ASP.NET Core MVC 把 request 透過 routing 送到 Controller action。Controller 透過 DI 呼叫 service，將 Entity 映射成符合頁面需求的 ViewModel，再用 `return View(model)` 交給 Razor View 產生 HTML。表單 POST 會經過 model binding 和 validation，失敗時檢查 `ModelState` 並回傳原 view；成功後使用 `RedirectToAction` 套用 Post-Redirect-Get，並用 Anti-Forgery Token 防止 cookie-based CSRF。Web API 通常繼承 `ControllerBase`，用 `Ok`、`Created` 回 JSON，和 MVC 的 Razor HTML response 不同。

## 7. 小練習

1. 把 `/Product/Details/10` 對應到 controller、action 和 `id`，並說明 model binding 發生在哪一步。
2. 把 `return View(model)`、`return Ok(model)`、`return RedirectToAction(nameof(Index))` 分別對應到 response body、status code 和下一個 request。
3. 寫一個 `ProductCreateViewModel`，要求名稱必填、長度最多 120、價格介於 `0.01` 和 `100000`。
4. 在 Create POST action 中故意送出空名稱，說明為什麼要 `return View(model)` 而不是 redirect。
5. 查看 Product sample render 出來的 HTML，找出 `<form>`、`__RequestVerificationToken`、`name="Name"` 和 `data-val-*`。
6. 把 sample 的 `UseSqlite` 改成 `UseSqlServer`，列出需要替換的 package、connection string 和 migration 步驟。
7. 為同一個 Product service 寫一個 MVC `Index` action 和一個 API `GET /api/products` action，保持各自的 ViewModel / DTO response boundary。
