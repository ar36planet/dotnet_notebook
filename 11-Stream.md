---
title: 11 Stream
tags: [csharp, stream, io, aspnet-core, upload]
---

# 11 `Stream`：檔案、request body 與 response body

## 學習目標

- 從 Java `InputStream` / `OutputStream` 理解 .NET `Stream`。
- 讀懂 `FileStream`、`MemoryStream`、HTTP content stream 的共同 abstraction。
- 在 ASP.NET Core 處理檔案上傳時使用 async copy 與 cancellation。

## 1. 一句話理解

`Stream` 是「依序讀取或寫入 bytes」的抽象；它讓同一套 API 可以處理檔案、記憶體、network body、request body 與 response body。

## 2. Java 對照

| C# | Java |
| --- | --- |
| `Stream` | `InputStream` / `OutputStream` 的共同概念，但 C# 同一型別同時提供 Read / Write 能力（實作可唯讀或唯寫） |
| `FileStream` | `FileInputStream` / `FileOutputStream` |
| `MemoryStream` | `ByteArrayInputStream` / `ByteArrayOutputStream` |
| `CopyToAsync` | loop read/write 或 transfer 類似的非同步抽象 |
| `IFormFile.OpenReadStream()` | multipart file part 的 input stream |

## 3. C# 語法

```csharp
await using var input = File.OpenRead("input.bin");
await using var output = File.Create("output.bin");

await input.CopyToAsync(output, cancellationToken);
```

### MemoryStream

```csharp
await using var memory = new MemoryStream();
await JsonSerializer.SerializeAsync(memory, value, cancellationToken: cancellationToken);

memory.Position = 0;
var copy = await JsonSerializer.DeserializeAsync<MyDto>(
    memory, cancellationToken: cancellationToken);
```

`Position` 很重要：寫完再讀之前通常要回到 0。不是所有 stream 都 seekable，network stream 可能不支援 `Position` / `Seek`。

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

        await using var input = file.OpenReadStream();
        await using var output = System.IO.File.Create(path);
        await input.CopyToAsync(output, cancellationToken);

        return Ok(new { name = safeName, size = file.Length });
    }
}
```

ASP.NET Core 的 model binding 會把 multipart request 的 file binding 到 `IFormFile`；你仍需負責：

- 大小上限與 quota。
- content type / magic bytes 驗證。
- 不相信原始檔名，避免 path traversal。
- 儲存到非 public web root 或 object storage。
- 病毒掃描、權限、保留期限與錯誤重試。

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

此時 framework 會協助把 stream 寫進 HTTP response；要確認 `_fileStore` 的 ownership contract，避免 stream 過早 dispose 或洩漏。

## 5. 常見誤解

- `Stream` 不一定可 seek，也不一定同時支援 read 與 write。
- 把整個 upload 讀成 `byte[]` 會以檔案大小消耗 memory；大型檔案優先 streaming。
- `MemoryStream` 用於短資料方便，但不是所有 payload 都該先放 memory。
- 寫完 stream 後要從頭讀，通常要設定 `Position = 0`；network stream 可能做不到。
- `CopyToAsync` 也要傳 token，否則 client disconnect 時可能還在複製。

## 6. 面試怎麼回答

> `Stream` 是 byte sequence 的抽象，`FileStream`、`MemoryStream`、HTTP body 都可以用同一組 read / write API。ASP.NET Core 上傳檔案時我會使用 `IFormFile.OpenReadStream()` 和 `CopyToAsync`，傳入 request cancellation token，並設定大小與安全驗證。大型檔案不要無腦轉成 byte array；同時要清楚誰擁有並負責 dispose stream。

## 7. 小練習

1. 寫一段把 input file 複製到 output file 的 async code。
2. 解釋為什麼 upload 不應直接使用 `file.FileName` 當儲存路徑。
3. `MemoryStream` 寫完後要讀，為什麼通常要設 `Position = 0`？
