---
title: 12 JSON 與序列化
tags: [aspnet-core, json, serialization, dto, yaml]
---

# 12 JSON 與序列化

## 學習目標

- 清楚區分 serialization、deserialization、model binding 與 DTO。
- 理解 ASP.NET Core 的 JSON request / response 自動流程。
- 知道 `System.Text.Json`、Newtonsoft.Json、YAML 的使用邊界。

## 1. 一句話理解

Serialization 把 object 轉成可傳輸或保存的格式；deserialization 把 JSON 等格式轉回 C# object，而 ASP.NET Core 會把這些步驟接進 model binding 與 response formatting。

## 2. Java 對照

| Java | C#／.NET | 備註 |
| --- | --- | --- |
| Jackson `ObjectMapper` | `JsonSerializer` + `JsonSerializerOptions` | 都負責 JSON 序列化與反序列化 |
| `@JsonProperty("display_name")` | `[JsonPropertyName("display_name")]` | 指定 JSON 欄位名稱 |
| `@JsonIgnore` | `[JsonIgnore]` | 排除欄位 |
| `@JsonTypeInfo`／`@JsonSubTypes` | `[JsonPolymorphic]`／`[JsonDerivedType]` | .NET 7+ 明確宣告多型階層 |

Jackson 預設常依 getter 命名產生 JSON；直接使用 `System.Text.Json` 預設保留 C# property 原名，ASP.NET Core MVC 與 Minimal API 則固定使用 Web defaults。

## 3. C# 語法

### 先看會出事的地方

直接使用 `JsonSerializer` 的預設設定，與 ASP.NET Core 回傳 JSON 使用的 Web defaults 不同：

```csharp
var dto = new NameDto("Ada");
var generalJson = JsonSerializer.Serialize(dto);
var generalCopy = JsonSerializer.Deserialize<NameDto>("{\"name\":\"Ada\"}");
var webCopy = JsonSerializer.Deserialize<NameDto>(
    "{\"name\":\"Ada\"}",
    new JsonSerializerOptions(JsonSerializerDefaults.Web));

Console.WriteLine(generalJson);
Console.WriteLine(generalCopy?.Name ?? "<null>");
Console.WriteLine(webCopy?.Name ?? "<null>");

public sealed record NameDto(string Name);
```

實際輸出：

```text
{"Name":"Ada"}
<null>
Ada
```

直接呼叫 `JsonSerializer` 預設使用 property 原名且大小寫敏感；ASP.NET Core 的 Web defaults 使用 camelCase、大小寫不敏感，並允許從字串讀取數字。

直接把 entity 當 response，資料庫欄位會跟著 API 暴露出去；之後新增 `PasswordHash` 或 navigation property，API contract 也可能在不知不覺中改變：

```csharp
// 不要直接把 persistence entity 回傳給 client
return Ok(user);

// 只建立 API 需要的 DTO
return Ok(new UserResponse(user.Id, user.Name, user.CreatedAt));
```

序列化器只負責把物件轉成 JSON，不判斷哪些欄位可以公開。DTO 先隔離 API contract，命名與 nullable 行為再由 serializer options 明確設定。

### serialize / deserialize

```csharp
var dto = new UserResponse(
    Guid.Parse("11111111-1111-1111-1111-111111111111"),
    "Ada",
    new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

string json = JsonSerializer.Serialize(dto);
UserResponse? copy = JsonSerializer.Deserialize<UserResponse>(json);
Console.WriteLine(json);
Console.WriteLine(copy);
```

實際輸出：

```text
{"Id":"11111111-1111-1111-1111-111111111111","Name":"Ada","CreatedAt":"2026-09-14T12:00:00+00:00"}
UserResponse { Id = 11111111-1111-1111-1111-111111111111, Name = Ada, CreatedAt = 2026/9/14 下午12:00:00 +00:00 }
```

### 命名與 attribute

```csharp
public sealed record UserResponse(
    Guid Id,
    [property: JsonPropertyName("display_name")] string Name,
    DateTimeOffset CreatedAt);
```

`[JsonPropertyName]` 改變 JSON property name，不改變 C# property name。ASP.NET Core MVC 與 Minimal API 固定使用 Web defaults：camelCase、大小寫不敏感；若契約是 `display_name`，用 attribute 或全域 naming policy 明確表達。

### options

```csharp
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

var json = JsonSerializer.Serialize(dto, options);
```

需要 enum 用字串、custom converter、ignore null 等行為時，透過 `JsonSerializerOptions`／converter 設定，而不是在每個呼叫點複製設定。options 會保存型別 metadata cache；應重用同一個 `static readonly` 或 DI instance，第一次序列化後不要再修改。

AOT 或 trimming 的 API 可以用 source generation：

```csharp
[JsonSerializable(typeof(UserResponse))]
internal partial class ApiJsonContext : JsonSerializerContext
{
}

var dto = new UserResponse(Guid.Empty, "Ada", DateTimeOffset.UtcNow);
string json = JsonSerializer.Serialize(dto, ApiJsonContext.Default.UserResponse);
```

上例需要 `System.Text.Json.Serialization` 的 using。`JsonSerializerContext` 讓編譯器產生型別 metadata，不必在執行期用反射建立。

## 4. 實務範例：ASP.NET Core request / response flow

```text
HTTP request JSON
    ↓ Content-Type: application/json
input formatter / System.Text.Json
    ↓ deserialization + model binding
CreateUserRequest request
    ↓ controller action 呼叫 service
UserResponse DTO
    ↓ output formatter / System.Text.Json
HTTP response JSON
```

Controller：

```csharp
public sealed record CreateUserRequest(
    string Name,
    string? PhoneNumber);

public sealed record UserResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt);

[HttpPost]
public async Task<ActionResult<UserResponse>> Create(
    CreateUserRequest request,
    CancellationToken cancellationToken)
{
    var created = await _service.CreateAsync(request, cancellationToken);
    return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
}
```

在 `[ApiController]` controller 中，框架會做四件事：

- 從 body 的 JSON deserialize 成 `CreateUserRequest`。
- 未註冊於 DI 的複雜型別推斷為 body，已註冊的複雜型別推斷為 service；名稱對到 route template 的參數走 route，其餘參數走 query。
- 若 body 無法解析或 validation model state invalid，依設定回傳 400 / validation problem response。
- 把 action 回傳的 DTO serialize 成 response JSON。

header 不在上述推斷清單中，要明確使用 `[FromHeader]`：

```csharp
public IActionResult Get(
    [FromHeader(Name = "X-Correlation-Id")] string? correlationId)
    => Ok(correlationId);
```

validation、授權、transaction 與敏感欄位的 DTO 設計仍要自己處理。

### `System.Text.Json` vs Newtonsoft.Json

- `System.Text.Json`：.NET 內建、ASP.NET Core 預設；應作為新 API 的第一選擇。多型自 .NET 7 起可用 `[JsonPolymorphic]`／`[JsonDerivedType]` 明確宣告。
- Newtonsoft.Json：需要 `TypeNameHandling`、JsonPath、寬鬆解析（單引號或無引號 property name）、舊套件 attribute 或相容性時才使用；ASP.NET Core 要裝 `Microsoft.AspNetCore.Mvc.NewtonsoftJson` 並呼叫 `AddNewtonsoftJson()`。
- 不要因為團隊習慣就混用兩套 serializer；同一個 API 的 naming、null、enum、date behavior 要一致。

多型的 .NET 10 寫法：

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(CardPayment), "card")]
public abstract record Payment(decimal Amount);

public sealed record CardPayment(decimal Amount, string LastFour)
    : Payment(Amount);
```

若未宣告可反序列化的衍生型別，`System.Text.Json` 會拒絕未知的型別 discriminator；它不等同 Newtonsoft.Json 的 `TypeNameHandling.All`。

### YAML

YAML 不是 `System.Text.Json` 的輸出格式，也不是 ASP.NET Core JSON pipeline 的預設。`.NET` 沒有內建 YAML serializer 或 configuration provider；需要時使用 YamlDotNet 等第三方套件。YAML 常見於部署、設定與手寫檔案；HTTP public API 若沒有明確需求，通常選 JSON。

## 5. 常見誤解

- DTO 隔離外部 contract 與 domain／persistence model。
- nullable compiler warning 不會自動讓 serializer 拒絕 null；.NET 9 起可以開啟 `RespectNullableAnnotations`，或在 .NET 10 使用 `JsonSerializerDefaults.Strict`：

  ```csharp
  var strict = new JsonSerializerOptions(JsonSerializerDefaults.Strict);
  JsonSerializer.Deserialize<CreateUserRequest>("{\"Name\":null}", strict);
  ```

  實際例外訊息：

  ```text
  System.Text.Json.JsonException: The constructor parameter 'Name' on type 'CreateUserRequest' doesn't allow null values. Consider updating its nullability annotation. Path: $.Name | LineNumber: 0 | BytePositionInLine: 12.
  ```

- JSON property name 由 naming policy、attribute 與 serializer options 決定；不會自動猜對所有契約。
- `System.Text.Json` 與 Newtonsoft.Json 的差異包含預設大小寫、預設命名、寬鬆解析與 `TypeNameHandling`；constructor、reference handling 與 .NET 7+ 的 attribute-based polymorphism 都有對應 API。

## 6. 面試怎麼回答

> ASP.NET Core 會把 request body 透過 input formatter 與 `System.Text.Json` deserialize，再做 model binding，把資料傳給 controller action。action 回傳 DTO 後，output formatter 再 serialize 成 JSON response。`System.Text.Json` 是目前預設，Newtonsoft.Json 適合特殊相容性與進階行為。DTO 的價值是隔離 API contract、domain entity 與資料庫 schema，避免過度暴露與不必要的耦合。

## 7. 小練習

1. 把 `Name` 在 JSON 中改名為 `display_name`。
2. 說明為什麼 `User` entity 不應直接當 public API response。
3. 寫出 JSON request → C# request DTO → service → response DTO 的四個階段。
