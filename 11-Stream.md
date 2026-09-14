---
title: 11 Stream
tags: [csharp, stream, io, aspnet-core, upload]
---

# 11 `Stream`：檔案、request body 與 response body

## 學習目標

- 讀懂 .NET `Stream` 如何依序處理 bytes。
- 讀懂 `FileStream`、`MemoryStream`、HTTP content stream 的共同 abstraction。
- 在 ASP.NET Core 處理檔案上傳時使用非同步複製並傳入 `CancellationToken`。

## 1. 一句話理解

`Stream` 是「依序讀取或寫入 bytes」的抽象；它讓同一套 API 可以處理檔案、記憶體、network body、request body 與 response body。

## 2. Java 對照

| Java | .NET | 差異 |
| --- | --- | --- |
| `InputStream`／`OutputStream` | `Stream` | .NET 用 `CanRead`、`CanWrite`、`CanSeek` 表示能力，不拆成兩個根類別 |
| `InputStream.read(byte[])` | `Stream.Read`／`ReadAsync` | Java EOF 回傳 `-1`；.NET EOF 回傳 `0`，且單次讀取可能少於要求數 |
| `InputStream.transferTo(OutputStream)` | `CopyToAsync` | 都把來源依序複製到目的地 |
| `InputStreamReader` + `BufferedReader` | `StreamReader` | 文字解碼與緩衝的包裝器 |
| try-with-resources | `using`／`await using` | scope 結束時釋放資源；多個資源按宣告反序釋放 |

## 3. C# 語法

### 先看會出事的地方

寫入記憶體 stream 後直接讀，讀取位置仍在尾端，結果會是空資料：

```csharp
await using var memory = new MemoryStream();
memory.Write("訂單 1001"u8);

using var reader = new StreamReader(memory);
var content = await reader.ReadToEndAsync();
Console.WriteLine($"內容：'{content}'");
Console.WriteLine($"Position={memory.Position}, Length={memory.Length}");
```

實際輸出：

```text
內容：''
Position=11, Length=11
```

修正方式是把可定位 stream 的 `Position` 設回 `0`：

```csharp
await using var memory = new MemoryStream();
memory.Write("訂單 1001"u8);
memory.Position = 0;

using var reader = new StreamReader(memory, leaveOpen: true);
Console.WriteLine($"內容：'{await reader.ReadToEndAsync()}'");
```

實際輸出：

```text
內容：'訂單 1001'
```

網路 stream 通常不可定位，不能假設所有 `Stream` 都能倒帶。

```csharp
CancellationToken cancellationToken = CancellationToken.None;
await using var input = new FileStream(
    "input.bin", FileMode.Open, FileAccess.Read, FileShare.Read,
    bufferSize: 81920, options: FileOptions.Asynchronous);
await using var output = new FileStream(
    "output.bin", FileMode.Create, FileAccess.Write, FileShare.None,
    bufferSize: 81920, options: FileOptions.Asynchronous);

await input.CopyToAsync(output, cancellationToken);
```

`File.OpenRead`／`File.Create` 開出的 `FileStream` 預設是同步 handle；上面用 `FileOptions.Asynchronous` 讓檔案 I/O 也採非同步選項。`CopyToAsync` 本身仍要傳入取消 token。

### MemoryStream

```csharp
await using var memory = new MemoryStream();
var value = new OrderSummary(1001, "A001");
var cancellationToken = CancellationToken.None;
await JsonSerializer.SerializeAsync(memory, value, cancellationToken: cancellationToken);

memory.Position = 0;
var copy = await JsonSerializer.DeserializeAsync<OrderSummary>(
    memory, cancellationToken: cancellationToken);

public sealed record OrderSummary(int Id, string Number);
```

寫完再讀之前要把 `Position` 設回 0；不可定位的 network stream 不支援 `Position`／`Seek`。

### `Read` 不保證一次讀滿

`Read` 回傳實際讀到的 byte 數，可能小於要求數，即使還沒到 EOF。固定長度資料可用 .NET 7+ 的 `ReadExactly`：

```csharp
using var source = new MemoryStream(new byte[10]);
var buffer = new byte[20];

Console.WriteLine(source.Read(buffer, 0, buffer.Length));
source.Position = 0;

try
{
    source.ReadExactly(buffer);
}
catch (EndOfStreamException exception)
{
    Console.WriteLine($"{exception.GetType().FullName}: {exception.Message}");
}
```

實際輸出：

```text
10
System.IO.EndOfStreamException: Unable to read beyond the end of the stream.
```

## 4. 實務範例：ASP.NET Core file upload

```csharp
[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return BadRequest("File is empty.");
        }

        // 不使用 user-supplied file.FileName 作為實體路徑。
        var safeName = Path.GetRandomFileName();
        var path = Path.Combine("uploads", safeName);
        Directory.CreateDirectory("uploads");

        await using var output = System.IO.File.Create(path);
        await file.CopyToAsync(output, cancellationToken);

        return Ok(new { name = safeName, size = file.Length });
    }
}
```

ASP.NET Core 的 model binding 會把 multipart request 的檔案綁定到 `IFormFile`。這是緩衝式上傳：64 KB 以下通常在記憶體，超過 64 KB 會移到暫存檔；`OpenReadStream()` 或 `CopyToAsync` 只是讀取這份已緩衝內容。真的要降低大型檔案的記憶體／暫存檔需求，需停用 form model binding，改用 `MultipartReader` 直接讀 `Request.Body`。

以下仍要自己處理：

- 大小上限與 quota。
- content type／檔頭 bytes 驗證。
- 不相信原始檔名，避免 path traversal。
- 儲存到網站根目錄外或物件儲存。

### response stream / proxy

```csharp
[HttpGet("{id:guid}")]
public async Task<IActionResult> Download(
    Guid id,
    CancellationToken cancellationToken)
{
    var stream = await _fileStore.OpenReadAsync(id, cancellationToken);
    return File(stream, "application/octet-stream", enableRangeProcessing: true);
}
```

`FileStreamResult` 會把 stream 寫進 HTTP response，回應送出後由 framework 釋放；action 裡不要再 `await using`。`_fileStore.OpenReadAsync` 必須交出這個 request 專用的 stream，不能回傳共用或稍後還要使用的 stream。

## 5. 常見誤解

- `Stream` 不一定可 seek，也不一定同時支援 read 與 write。
- `Read` 回傳值可能小於要求數；要讀滿固定長度用 .NET 7+ 的 `ReadExactly`，否則自行 loop 到回傳 0。
- `StreamReader`／`StreamWriter` dispose 時預設會關閉底層 stream；要繼續使用底層 stream，傳 `leaveOpen: true`。
- `Request.Body` 預設只能向前讀一次；要讓 middleware 與後續 model binding 都能讀，在第一次讀之前呼叫 `Request.EnableBuffering()`，讀完把 `Position` 設回 0：

  ```csharp
  Request.EnableBuffering();
  using var reader = new StreamReader(Request.Body, leaveOpen: true);
  var rawBody = await reader.ReadToEndAsync(cancellationToken);
  Request.Body.Position = 0;
  ```

- `IFormFile` 是緩衝式上傳，不是真正的大檔串流；真正的串流路徑使用 `MultipartReader`。

## 6. 面試怎麼回答

> `Stream` 是位元組序列的抽象，`FileStream`、`MemoryStream`、HTTP body 都可以用同一組 read／write API。`IFormFile` 是緩衝式上傳；需要直接處理大檔時，我會用 `MultipartReader` 讀 `Request.Body`。若使用 `IFormFile`，則呼叫 `CopyToAsync` 並傳入 `CancellationToken`，同時設定大小與安全驗證；回應下載的 stream 則交給 `FileStreamResult` 釋放。

## 7. 小練習

1. 寫一段把 input file 複製到 output file 的 async code。
2. 解釋為什麼 upload 不應直接使用 `file.FileName` 當儲存路徑。
3. `MemoryStream` 寫完後要讀，為什麼通常要設 `Position = 0`？
