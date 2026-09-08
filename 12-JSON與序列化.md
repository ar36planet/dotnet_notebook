---
title: 12 JSON 與序列化
tags: [aspnet-core, json, serialization, dto, yaml]
---

# 12 JSON / YAML Serialization

## 學習目標

- 清楚區分 serialization、deserialization、model binding 與 DTO。
- 理解 ASP.NET Core 的 JSON request / response 自動流程。
- 知道 `System.Text.Json`、Newtonsoft.Json、YAML 的使用邊界。

## 1. 一句話理解

Serialization 把 object 轉成可傳輸或保存的格式；deserialization 把 JSON 等格式轉回 C# object，而 ASP.NET Core 會把這些步驟接進 model binding 與 response formatting。

## 2. Java 對照

| C# / .NET | Java / Spring |
| --- | --- |
| `System.Text.Json` | Jackson / Gson 的角色 |
| `JsonSerializer` | `ObjectMapper` 類似角色 |
| `[JsonPropertyName]` | `@JsonProperty` |
| DTO record | Java record / DTO class |
| model binding | Spring MVC argument binding + Jackson conversion |
| Newtonsoft.Json | Json.NET；功能成熟、相容性廣，但 ASP.NET Core 預設不是它 |

## 3. C# 語法

### serialize / deserialize

```csharp
var dto = new UserDto(Guid.NewGuid(), "Ada", DateTimeOffset.UtcNow);

string json = JsonSerializer.Serialize(dto);
UserDto? copy = JsonSerializer.Deserialize<UserDto>(json);
```

### 命名與 attribute

```csharp
public sealed record UserResponse(
    Guid Id,
    [property: JsonPropertyName("display_name")] string Name,
    DateTimeOffset CreatedAt);
```

`[JsonPropertyName]` 改變 JSON property name，不改變 C# property name。ASP.NET Core web defaults 通常使用 camelCase，例如 `createdAt`；若契約是 `display_name`，用 attribute 或全域 naming policy 明確表達。

### options

```csharp
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

var json = JsonSerializer.Serialize(dto, options);
```

需要 enum 用字串、custom converter、ignore null 等 behavior 時，透過 `JsonSerializerOptions` / converter 設定，而不是在每個 call site 複製設定。

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

在 `[ApiController]` controller 中，ASP.NET Core 會協助：

- 從 body 的 JSON deserialize 成 `CreateUserRequest`。
- 根據 route、query、header、service DI 等來源做 parameter binding。
- 若 body 無法解析或 validation model state invalid，依設定回傳 400 / validation problem response。
- 把 action 回傳的 DTO serialize 成 response JSON。

Framework 不會自動替你完成 domain validation、授權、資料庫 transaction、DTO 是否不暴露敏感欄位等設計責任。

### `System.Text.Json` vs Newtonsoft.Json

- `System.Text.Json`：.NET 內建、ASP.NET Core 預設、效能與 source generation 路線較好，應作為新 API 的第一選擇。
- Newtonsoft.Json：在 polymorphic serialization、legacy settings、特殊 converter、舊套件相容性等場景仍常見；使用前先確認必要性與 security 設定。
- 不要因為團隊習慣就混用兩套 serializer；同一個 API 的 naming、null、enum、date behavior 要一致。

### YAML

YAML 不是 `System.Text.Json` 的輸出格式，也不是 ASP.NET Core JSON pipeline 的預設。`.NET` 專案可用例如 YamlDotNet 或 configuration provider 處理 YAML：

```csharp
// 具體套件 API 依版本而異；概念上是 serializer + DTO。
var settings = yamlDeserializer.Deserialize<AppSettings>(yamlText);
```

YAML 常見於 deployment / configuration / human-authored files；HTTP public API 若沒有明確需求，通常選 JSON。

## 5. 常見誤解

- DTO 不是單純為了「讓 JSON 長得漂亮」；它隔離外部 contract 與 domain / persistence model。
- nullable compiler warning 不等於 serializer 一定會拒絕 null；必須設定與 validation policy。
- JSON property name mapping 不是 JavaScript 自動魔法；要看 naming policy、attribute 與 serializer options。
- `System.Text.Json` 與 Newtonsoft.Json 的 default behavior 不完全相同，尤其是 constructor、polymorphism、reference handling。
- 不要把 entity 直接 expose 給 API；navigation properties 可能造成循環、過度傳輸或資料洩漏。

## 6. 面試怎麼回答

> ASP.NET Core 會把 request body 透過 input formatter 與 `System.Text.Json` deserialize，再做 model binding，把資料傳給 controller action。action 回傳 DTO 後，output formatter 再 serialize 成 JSON response。`System.Text.Json` 是目前預設，Newtonsoft.Json 適合特殊相容性與 advanced behavior。DTO 的價值是隔離 API contract、domain entity 與資料庫 schema，避免過度暴露與不必要的耦合。

## 7. 小練習

1. 把 `Name` 在 JSON 中改名為 `display_name`。
2. 說明為什麼 `User` entity 不應直接當 public API response。
3. 寫出 JSON request → C# request DTO → service → response DTO 的四個階段。
