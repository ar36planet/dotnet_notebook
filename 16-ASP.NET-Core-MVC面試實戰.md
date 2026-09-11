---
title: 16 ASP.NET Core MVC 面試實戰
tags: [aspnet-core, mvc, razor, interview, model-binding, validation]
---

# 16 ASP.NET Core MVC 面試實戰

## 學習目標

- 用 30 秒說清楚 MVC、Razor、model binding、validation 和 PRG。
- 看到 controller、view、ViewModel 時，能說明每一行在 HTTP flow 的位置。
- 能用同一個 Product domain 比較 MVC HTML response 和 Web API JSON response。
- 知道常見追問的邊界，不把 ASP.NET MVC 5、ASP.NET Core MVC 和 Web API 混成一套。

## 1. 一句話理解

MVC 面試題大多在問同一件事：瀏覽器送進來的資料如何經過 routing、model binding、validation、service 和 EF Core，最後變成 Razor HTML 或 redirect。

## 2. Java 對照

Spring MVC 的 `@Controller`、form object、Bean Validation、Thymeleaf fragment 和 redirect attributes，可以幫助定位 ASP.NET Core MVC 的概念；實際 API 仍以 `Controller`、`ModelState`、Razor、Tag Helpers 和 `TempData` 為準。

## 3. C# 語法

面試中常見的 MVC 寫法：

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

    await _service.CreateAsync(model, cancellationToken);
    return RedirectToAction(nameof(Index));
}
```

這段 code 同時包含 HTTP method、Anti-Forgery、model binding、validation、ViewModel、service、PRG 和 action result，是回答多題時的共同例子。

## 4. 實務範例：19 題面試題

### 1. MVC 是什麼？

**30 秒面試回答**

> MVC 把應用程式分成 Model、View、Controller。Controller 是 HTTP 入口，接收 request、呼叫 service、選擇 response；Model 表示資料和 domain 行為；View 使用 Razor 把 ViewModel render 成 HTML。ASP.NET Core MVC 的重點是 routing、model binding、validation、DI 和 view rendering，不是把所有資料庫邏輯塞進 controller。

**詳細解釋**

以商品清單為例，`ProductController.Index` 取得商品，service 把 Entity 映射成 `ProductViewModel`，`Views/Product/Index.cshtml` 用 `@foreach` 產生 table。View 不應自己注入 `DbContext` 查詢資料，Controller 也不應承擔所有 domain rule。

**常見追問**

追問：Model 一定只能是 Entity 嗎？

回答：不一定。MVC 的 Model 是廣義概念；實務上會把 persistence Entity、form ViewModel、read ViewModel 分開，避免頁面欄位和資料庫 schema 綁死。

**程式碼範例**

```csharp
public sealed class ProductController(IProductService service)
    : Controller
{
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
        => View(await service.ListAsync(cancellationToken));
}
```

### 2. `Controller` 和 `ControllerBase` 有什麼差異？

**30 秒面試回答**

> `Controller` 繼承 `ControllerBase`，再提供 `View()`、`ViewData`、`ViewBag`、`TempData`、`PartialView()` 等 MVC View helpers。`ControllerBase` 適合只回 status code、JSON、file 等 API response。兩者都能使用 DI、routing、filters 和 model binding；主要差別是是否需要 Razor View pipeline。

**詳細解釋**

`[ApiController]` 常和 `ControllerBase` 一起使用，會帶來 API 導向的 binding 和 validation 行為。瀏覽器表單需要 validation 失敗時重新 render `.cshtml`，通常使用 `Controller`，自己檢查 `ModelState`。

**常見追問**

追問：`Controller` 可以回 JSON 嗎？

回答：可以，因為它繼承 `ControllerBase`，可以 `return Ok(model)` 或 `return Json(model)`；但只回 JSON 的 controller 通常使用 `ControllerBase`，讓意圖更清楚。

**程式碼範例**

```csharp
public sealed class ProductController : Controller
{
    public IActionResult Page() => View();
}

[ApiController]
[Route("api/products")]
public sealed class ProductsApiController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(Array.Empty<ProductResponse>());
}
```

### 3. MVC 與 Web API 有什麼差異？

**30 秒面試回答**

> MVC 主要服務瀏覽器頁面，`Controller` 把 ViewModel 交給 Razor，回 HTML；Web API 主要服務 SPA、mobile 或其他 application，`ControllerBase` 把 DTO 交給 formatter，回 JSON。兩者可以共用 service、repository、EF Core 和 DI，但 response contract、authentication 取向和錯誤處理不必相同。

**詳細解釋**

同一個 Product 功能可以有：

```text
GET /Product       → HTML
GET /api/products  → JSON
```

MVC 常見 cookie / session / TempData；API 常見 bearer token 和明確的 status code / JSON error contract。這不是絕對規則，而是 client 形狀不同造成的預設設計。

**常見追問**

追問：MVC 和 API 能在同一個專案嗎？

回答：可以。使用 `AddControllersWithViews()`、`MapControllerRoute()` 提供 MVC，再用 `MapControllers()` 或 API route 提供 attribute-routed API。

**程式碼範例**

```csharp
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();
```

### 4. Razor View 是什麼？

**30 秒面試回答**

> Razor View 是 `.cshtml` server-side template，HTML 是主要內容，`@` 用來嵌入 C#。`@model` 宣告 view 的強型別 model，`@Model` 讀取 controller 傳入的資料。MVC 在 server 執行 Razor，最後只把一般 HTML 傳給瀏覽器。

**詳細解釋**

`return View(model)` 不會回傳 `.cshtml` 原始檔；MVC 會做 view discovery，先找 `Views/[Controller]/[Action].cshtml`，再由 Razor render。預設輸出會做 HTML encoding，避免把一般文字誤當成 HTML。

**常見追問**

追問：Razor View 可以寫商業邏輯嗎？

回答：可以寫顯示所需的 `if` 和 `foreach`，但不應在 view 查 DB 或實作訂單規則。複雜顯示邏輯應移到 ViewModel、service 或 View Component。

**程式碼範例**

```cshtml
@model ProductViewModel

<h1>@Model.Name</h1>
<p>@Model.Price.ToString("C2")</p>
```

### 5. ViewModel 為什麼不直接使用 Entity？

**30 秒面試回答**

> Entity 代表資料庫和 domain state，ViewModel 代表某一個畫面需要輸入或輸出的欄位。直接 bind Entity 會暴露內部欄位、造成 overposting，也讓資料庫 schema 綁住 UI。Create、Edit、List、Details 通常應使用不同 ViewModel，再由 service 或 controller 做 mapping。

**詳細解釋**

如果 `Product` 有 `IsDiscontinued`、`CreatedAt`、`InternalCost`，Create form 不應讓使用者提交這些欄位。`ProductCreateViewModel` 只放名稱和售價，欄位本身就是 allow-list。

**常見追問**

追問：用 `[Bind]` 排除欄位不就好了？

回答：`[Bind]` 可以降低風險，但專用 ViewModel 通常更清楚，也能分別表達 Create 和 Edit 的欄位規則；不要把安全邊界只交給一個 attribute。

**程式碼範例**

```csharp
public sealed class ProductCreateViewModel
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 100000)]
    public decimal Price { get; set; }
}
```

### 6. Model Binding 是什麼？

**30 秒面試回答**

> Model binding 會從 route data、query string、form fields、request body 或 uploaded files 取得值，轉成 action 參數或 ViewModel。`/Product/Edit/10` 的 `10` 可以 binding 成 `int id`；form 的 `Name` 和 `Price` 可以 binding 成 `ProductCreateViewModel`。轉換或 validation 的錯誤會放進 `ModelState`。

**詳細解釋**

需要明確來源時使用 `[FromRoute]`、`[FromQuery]`、`[FromForm]`、`[FromBody]`。MVC 表單通常從 form binding；API JSON body 使用 input formatter。`[FromBody]` 不要在同一 action 放兩個，因為 body 通常只能讀一次。

**常見追問**

追問：`[FromBody]` 和 `[FromForm]` 差在哪？

回答：`[FromBody]` 由 input formatter 解析整個 request body，例如 JSON；`[FromForm]` 讀 form fields，例如 `application/x-www-form-urlencoded` 或 multipart form。

**程式碼範例**

```csharp
public IActionResult Details([FromRoute] int id)
    => View(id);

[HttpPost]
public IActionResult Create(
    [FromForm] ProductCreateViewModel model)
    => !ModelState.IsValid ? View(model) : RedirectToAction(nameof(Index));
```

### 7. `ModelState` 是什麼？

**30 秒面試回答**

> `ModelState` 保存 model binding 和 model validation 的結果。它不只記錄 `[Required]` 或 `[Range]`，把字串轉成 `decimal` 失敗也會記錄 error。MVC action 通常在 `!ModelState.IsValid` 時 `return View(model)`，讓 Validation Tag Helpers 把錯誤顯示回表單。

**詳細解釋**

驗證失敗不能 redirect，因為 `ModelState` 是目前 request 的資料。直接回相同 view 才能保留輸入值和欄位錯誤。

**常見追問**

追問：可以自己加入 ModelState error 嗎？

回答：可以。跨欄位或查資料庫才能知道的錯誤，可以 `ModelState.AddModelError(nameof(model.Name), "商品名稱已存在。");`，再回傳 view。

**程式碼範例**

```csharp
if (await service.ExistsAsync(model.Name, cancellationToken))
{
    ModelState.AddModelError(
        nameof(model.Name),
        "商品名稱已存在。");
}

if (!ModelState.IsValid)
{
    return View(model);
}
```

### 8. `View()` 和 `RedirectToAction()` 差在哪？

**30 秒面試回答**

> `View(model)` 在目前 request 內建立 `ViewResult`，執行 Razor 並回 HTML；`RedirectToAction` 回傳 redirect result，通常是 `302` 加 `Location`，瀏覽器再發一個新的 GET。表單驗證失敗用 `View(model)` 保留錯誤，POST 成功用 `RedirectToAction` 套用 PRG。

**詳細解釋**

```text
return View(model)            → 一個 request，通常 200 HTML
return RedirectToAction(...)  → 302，再一個 GET，通常最後 200 HTML
```

`RedirectToAction` 不等於在 server 直接呼叫另一個 C# method；它是 HTTP response。

**常見追問**

追問：`return View("Index")` 可以取代 redirect 嗎？

回答：可以 render 清單，但仍是 POST response。重新整理可能重送 POST；而且若要重新查清單，還可能把查詢邏輯塞回 POST action。成功 mutation 後優先使用 PRG。

**程式碼範例**

```csharp
if (!ModelState.IsValid)
{
    return View(model);
}

await service.CreateAsync(command, cancellationToken);
return RedirectToAction(nameof(Index));
```

### 9. `TempData`、`ViewData`、`ViewBag` 差在哪？

**30 秒面試回答**

> `Model` 是 view 的主要強型別資料；`ViewData` 是目前 request 的 dictionary；`ViewBag` 是動態存取 `ViewData` 的 wrapper；`TempData` 用來把小資料帶過下一個 request，常用在 POST redirect 後顯示成功訊息。ViewData 和 ViewBag 不會自然跨 redirect，TempData 會。

**詳細解釋**

```csharp
ViewData["Title"] = "商品清單";
ViewBag.Title = "商品清單";
TempData["Message"] = "商品已建立。";
```

`ViewBag.Title` 和 `ViewData["Title"]` 寫的是同一個 request-level dictionary；`ViewBag` 沒有 compile-time property 檢查。頁面主要資料應使用 `@model`，不要用 ViewBag 傳整個畫面的核心資料。

**常見追問**

追問：TempData 可以存 Entity 嗎？

回答：不適合。它是跨 request 的暫存機制，預設 provider 常用 cookie，應只放短小訊息或識別值；大物件和敏感資料應放 service / database / distributed cache。

**程式碼範例**

```csharp
TempData["Message"] = "商品已更新。";
return RedirectToAction(nameof(Index));
```

```cshtml
@if (TempData["Message"] is string message)
{
    <div class="alert alert-success">@message</div>
}
```

### 10. Tag Helper 是什麼？

**30 秒面試回答**

> Tag Helper 讓 server-side code 透過 HTML attribute 參與 Razor rendering。例如 `asp-action` 產生 action URL，`asp-route-id` 產生 route value，`asp-for` 依 ViewModel metadata 產生 input 的 `id`、`name`、value 和 validation attributes。瀏覽器收到的是一般 HTML，不會看到 `asp-for`。

**詳細解釋**

```cshtml
<a asp-action="Details" asp-route-id="@item.Id">詳細</a>
```

在 `item.Id == 10`、default route 下，輸出接近：

```html
<a href="/Product/Details/10">詳細</a>
```

它比手寫 `/Product/Details/10` 更能跟 route configuration 一起變更，也避免把 controller / action 名稱散落在字串 URL。

**常見追問**

追問：Tag Helper 和 HTML Helper 一樣嗎？

回答：目的相近，但寫法不同。Tag Helper 是 HTML attribute / element syntax；HTML Helper 是 `@Html.BeginForm`、`@Html.EditorFor` 這類 C# method。既有專案兩者都可能存在。

**程式碼範例**

```cshtml
<form asp-action="Create" method="post">
    <input asp-for="Name" />
    <span asp-validation-for="Name"></span>
</form>
```

### 11. Partial View 是什麼？

**30 秒面試回答**

> Partial View 是沒有獨立 action flow 的可重用 `.cshtml` 片段，適合拆出重複 HTML，例如 validation script、商品列或作者資訊。它不應自己查資料庫；需要 server-side code、參數和 service 時，可以使用 View Component。共用整站外框則使用 Layout。

**詳細解釋**

Partial View 可以接收自己的 model，也會在父 view 的 rendering 中產生 HTML。現代寫法可以使用 Partial Tag Helper 或 `PartialAsync`；不要把 Partial 當成 controller。

**常見追問**

追問：Partial View 和 View Component 怎麼選？

回答：只是 markup reuse 用 partial；需要執行查詢或複雜 UI logic 用 View Component；整頁共用 header/footer 用 Layout。

**程式碼範例**

```cshtml
@section Scripts {
    <partial name="_ValidationScriptsPartial" />
}
```

### 12. Layout 是什麼？

**30 秒面試回答**

> Layout 是共用 HTML 外框，通常放 head、navigation、footer 和 script。內容 view 用 `@RenderBody()` 插入主要內容；需要額外 JavaScript 時用 `@section Scripts`，Layout 用 `RenderSectionAsync` 決定是否 render。這讓每個 `.cshtml` 不必重複整份 HTML document。

**詳細解釋**

`Views/_ViewStart.cshtml` 常設定 `Layout = "_Layout"`，因此每個 action view 都套用同一個 layout。Section 只直接屬於 content view 和 immediate layout，不能從 partial view 任意向上宣告 section。

**常見追問**

追問：Layout 和 Partial View 都能重用 HTML，差在哪？

回答：Layout 定義整頁外框和 body insertion point；Partial 是 body 裡的一段可重用內容。Partial 不負責整頁 `<html>`、`<head>` 或 `RenderBody`。

**程式碼範例**

```cshtml
<!-- _Layout.cshtml -->
@RenderBody()
@await RenderSectionAsync("Scripts", required: false)
```

### 13. Anti-Forgery Token 是做什麼的？

**30 秒面試回答**

> CSRF 利用瀏覽器會自動帶 cookie 的特性，讓另一個網站代替使用者 POST 到我們的 MVC app。Form Tag Helper 會產生 Anti-Forgery hidden field，POST action 用 `[ValidateAntiForgeryToken]` 驗證。它防的是跨站偽造請求，不是 authentication、authorization 或一般 input validation。

**詳細解釋**

攻擊者可以知道 `/Product/Delete/10`，但通常不能讀取同源頁面產生的不可預測 token。沒有合法 token 時，表單 action 不應照正常流程執行。

**常見追問**

追問：為什麼 API 常不寫這個 attribute？

回答：要看 API 的 authentication。若 API 使用 bearer token，瀏覽器不會像 cookie 一樣自動附加該 token，CSRF 模型不同；若 API 仍靠 cookie 且允許瀏覽器跨站呼叫，就要依風險啟用 CSRF 防護。

**程式碼範例**

```cshtml
<form asp-action="Create" method="post">
    <input asp-for="Name" />
    <button type="submit">儲存</button>
</form>
```

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult Create(ProductCreateViewModel model)
    => !ModelState.IsValid ? View(model) : RedirectToAction(nameof(Index));
```

### 14. PRG Pattern 是什麼？

**30 秒面試回答**

> PRG 是 Post → Redirect → Get。POST 成功後回 `RedirectToAction(nameof(Index))`，瀏覽器依 `Location` 再發 GET。這會讓重新整理只重複 GET，不會再次送出建立或更新表單；POST 失敗則直接 `return View(model)`，保留 ModelState 和輸入值。

**詳細解釋**

```text
POST /Product/Create → 302 Location: /Product
GET /Product         → 200 text/html
```

`TempData` 正好適合在 302 之後把「已儲存」訊息帶給下一個 GET。

**常見追問**

追問：為什麼 validation failure 不用 redirect？

回答：`ModelState`、binding error 和使用者剛填的值是目前 request 的 state；直接回 View 才能顯示。若 redirect，必須自己把錯誤和輸入值重新序列化保存，容易遺失。

**程式碼範例**

```csharp
if (!ModelState.IsValid)
{
    return View(model);
}

await service.CreateAsync(command, cancellationToken);
TempData["Message"] = "商品已建立。";
return RedirectToAction(nameof(Index));
```

### 15. MVC Controller 如何取得 Route、Query、Form 的資料？

**30 秒面試回答**

> Route parameter 用 `[FromRoute]`，query string 用 `[FromQuery]`，表單欄位用 `[FromForm]` 或 complex model binding，JSON body 用 `[FromBody]`。例如 `/Product/Edit/10?tab=pricing` 可以把 `10` binding 成 `id`，把 `pricing` binding 成 `tab`，POST form 的 `Name` 和 `Price` binding 成 ViewModel。

**詳細解釋**

```csharp
public IActionResult Edit(
    [FromRoute] int id,
    [FromQuery] string? tab)
    => View(new ProductEditPageModel(id, tab));

[HttpPost]
public IActionResult Edit(
    [FromRoute] int id,
    [FromForm] ProductEditViewModel model)
    => View(model);
```

Route / query 常用於簡單型別；form 能填充 complex object；body 交給 input formatter。不同來源同名時，不要靠猜，使用 attribute 表明意圖。

**常見追問**

追問：`[ApiController]` 下 binding 有何不同？

回答：它會套用 API 常見的 parameter source inference 和 automatic validation response；MVC form action 通常希望 validation failure 回原本 `.cshtml`，所以不要把兩種行為混為一談。

**程式碼範例**

```csharp
[HttpPost("api/products")]
public IActionResult CreateApi(
    [FromBody] CreateProductRequest request)
    => Ok(request);
```

### 16. MVC 如何進行表單 Validation？

**30 秒面試回答**

> Model binding 將字串轉成 ViewModel 後，MVC 執行 Data Annotations 和其他 validator，結果放進 `ModelState`。Action 檢查 `ModelState.IsValid`；View 用 `asp-validation-summary` 和 `asp-validation-for` 顯示錯誤。client-side validation 只是改善 UX，server-side validation 仍然必要。

**詳細解釋**

validation 可能在三個地方發現問題：欄位缺少、值不符合 annotation、字串轉型失敗。跨欄位或需要查資料庫的規則，通常在 service 或 action 額外加入 `ModelState` error，domain rule 仍應在 service / domain 層保護。

**常見追問**

追問：為什麼頁面有 JavaScript validation 還要 server validation？

回答：使用者可以關掉 JavaScript，也可以直接用 curl 或 API client 送 request；只有 server validation 才是可信任的邊界。

**程式碼範例**

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

```cshtml
<div asp-validation-summary="ModelOnly"></div>
<span asp-validation-for="Name"></span>
```

### 17. MVC 如何與 EF Core 整合？

**30 秒面試回答**

> 在 `Program.cs` 用 `AddDbContext` 註冊 scoped `DbContext`，Controller 注入 service，service 再呼叫 repository 或直接使用 application data access。讀取清單通常用 `AsNoTracking`、projection 和 `ToListAsync`；更新則查 tracking entity、修改欄位、呼叫 `SaveChangesAsync`。ViewModel 和 Entity 之間要明確 mapping。

**詳細解釋**

```text
Controller → Service → Repository → DbContext → SQL Server
SQL Server → DbContext → Entity → Service mapping → ViewModel → Razor
```

`DbContext` 不應是 singleton，因為它包含 request-scoped tracking state。read query 和 write query 的 tracking 需求不同。

**常見追問**

追問：MVC 一定要 Repository pattern 嗎？

回答：不一定。小型 app 可以由 service 直接依賴 `DbContext`；如果需要隔離 persistence、替換資料來源或讓邊界更清楚，再抽 repository。Repository 不是每張 table 都必須包一層的規則。

**程式碼範例**

```csharp
public async Task<IReadOnlyList<Product>> ListAsync(
    CancellationToken cancellationToken)
    => await db.Products
        .AsNoTracking()
        .OrderBy(product => product.Id)
        .ToListAsync(cancellationToken);
```

### 18. MVC 和 Web API 可以存在同一個 ASP.NET Core 專案嗎？

**30 秒面試回答**

> 可以。`AddControllersWithViews` 提供 Razor MVC，`MapControllerRoute` 提供 conventional page routes；API controller 可以使用 `ControllerBase`、`[ApiController]` 和 `MapControllers`。兩者可以共用 DI、service、repository 和 EF Core，但應維持 HTML ViewModel 與 JSON DTO 的 response boundary。

**詳細解釋**

一個 app 可以同時提供：

```text
/Product             → ProductController → HTML
/api/products        → ProductsApiController → JSON
```

middleware、authentication、authorization 可以共用，但表單 Anti-Forgery 和 API token / CORS 要依 endpoint 的 client 形狀設定。

**常見追問**

追問：`AddControllers()` 能 render MVC View 嗎？

回答：一般 MVC View 專案應使用 `AddControllersWithViews()`；`AddControllers()` 主要註冊 controller、API explorer、formatter 等，不包含完整 View rendering setup。

**程式碼範例**

```csharp
builder.Services.AddControllersWithViews();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();
```

### 19. Razor Pages 和 MVC 有什麼差異？

**30 秒面試回答**

> MVC 以 Controller action 為 request entry point，view 通常依 controller / action 放在 `Views`；Razor Pages 以 `.cshtml` page 為中心，PageModel 放在同一個 page 的 code-behind，使用 `OnGet`、`OnPost` handler。兩者都使用 Razor、DI、model binding、validation、filters 和 middleware。既有 controller / view 大型專案常維持 MVC；以頁面為單位的新功能可以評估 Razor Pages。

**詳細解釋**

MVC：

```text
Controllers/ProductController.cs
Views/Product/Edit.cshtml
```

Razor Pages：

```text
Pages/Products/Edit.cshtml
Pages/Products/Edit.cshtml.cs  // PageModel
```

Razor Pages 把 page-specific handler 和 page model 靠近，頁面導向功能通常比較直接；MVC 對 controller action grouping、既有企業架構、同一 controller 管理多個 related actions 比較熟悉。兩者不是 API 與 HTML 的二分法；Razor Pages 仍是 server-rendered HTML programming model。

**常見追問**

追問：Razor Pages 會取代 MVC 嗎？

回答：不必用取代的角度理解。選擇取決於團隊既有架構、功能是 page-oriented 還是 controller/action-oriented，以及是否需要維持既有 MVC route 和 view 組織。API 專案通常不因為有 Razor Pages 就改用它。

**程式碼範例**

```csharp
// Razor Pages 的 PageModel handler
public sealed class EditModel : PageModel
{
    public void OnGet(int id) { }

    public IActionResult OnPost(ProductEditViewModel model)
        => !ModelState.IsValid
            ? Page()
            : RedirectToPage("Index");
}
```

## 5. 常見誤解

- `Controller`、`ControllerBase`、`[ApiController]` 是三個不同層次的概念：base class、View helper 能力、API behavior convention 不要混成一個名詞。
- `ViewBag` 沒有比 ViewModel 更強的型別安全；頁面的主要資料應使用 `@model`。
- Tag Helper 不是前端 JavaScript component；它在 server render 階段產生 HTML。
- `ModelState.IsValid` 不是 domain rule 的全部；service 仍要檢查商品重複、庫存、權限等規則。
- `RedirectToAction` 不是直接呼叫 action method；它會產生 redirect response。
- Partial View 不是輕量 Controller；有 server-side query 時考慮 View Component 或 service。
- Anti-Forgery Token 不能取代 authorization；有合法 token 的使用者仍可能沒有刪除商品權限。
- MVC 和 Web API 可以共存，也不代表所有 ViewModel 都應直接拿去當 API DTO。

## 6. 面試怎麼回答

面試回答建議固定順序：先講 response 形狀，再講 request flow，最後補安全和分層。

> 我會先判斷這是瀏覽器頁面還是 API。MVC 頁面通常由 `Controller` action 接收 route、query 或 form，model binding 建立 ViewModel，`ModelState` 驗證失敗就回 `View(model)`，成功則呼叫 service、寫入 EF Core，最後 `RedirectToAction` 套用 PRG。Razor View 透過 Tag Helpers 產生 URL、form、input 和 validation HTML；POST action 加 Anti-Forgery Token。API 則通常由 `ControllerBase` 回 DTO 和 JSON，兩邊可以共用 service，但不共用 response boundary。

## 7. 小練習

1. 不看資料，解釋 `/Product/Edit/10?tab=pricing` 的 route、query 和 action parameter 如何對應。
2. 為 `ProductCreateViewModel` 加上 `[Compare]`，並說明錯誤如何進入 `ModelState`。
3. 寫出一個同時包含 `ProductController` 和 `ProductsApiController` 的 `Program.cs` route 設定。
4. 解釋為什麼 POST 成功使用 redirect，validation 失敗使用 `return View(model)`。
5. 用 30 秒回答：「為什麼不能把 EF Core Entity 直接當成 MVC form model？」
6. 讀 `examples/ProductMvc` 的完整程式碼，指出每一個 `ToListAsync`、`SaveChangesAsync` 和 `return View` 在 request flow 的位置。
