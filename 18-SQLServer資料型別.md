---
title: 18 SQL Server 資料型別
tags: [sql-server, mssql, data-types, csharp, ef-core]
---

# 18 SQL Server 資料型別

## 學習目標

- 從應用程式與 EF Core 邊界理解 SQL Server 型別，而不只是背名稱。
- 能在 `varchar`、`nvarchar`、`datetime2`、`datetimeoffset`、`decimal(p,s)` 之間做合理選擇。
- 知道 SQL Server type 與 C# type 不是一對一轉換。

## 1. 一句話理解

資料型別是資料庫的 contract：它決定可儲存的值、比較與排序方式、精度、索引大小、NULL 行為與 ORM mapping；C# 的 `string` 或 `DateTime` 不會替你完成這些決策。

## 2. 實際 SQL

```sql
CREATE TABLE Orders
(
    OrderId         uniqueidentifier NOT NULL,
    InvoiceNo       varchar(20) NULL,
    CustomerName    nvarchar(200) NOT NULL,
    Amount          decimal(19, 4) NOT NULL,
    IsPaid          bit NOT NULL,
    OrderedAt       datetime2(7) NOT NULL,
    PaidAt          datetimeoffset(7) NULL,
    ReceiptPdf      varbinary(max) NULL,
    CONSTRAINT PK_Orders PRIMARY KEY (OrderId)
);
```

### SQL Server ↔ C# 常見對照

| SQL Server | C# 常見對應 | 重要提醒 |
| --- | --- | --- |
| `int` | `int` | 32-bit signed integer |
| `bigint` | `long` | 64-bit signed integer |
| `bit` | `bool` | SQL `NULL` 時 C# 通常是 `bool?` |
| `varchar(n)` | `string` | 依 collation 決定編碼；SQL Server 2019+ 的 `_UTF8` collation 可存完整 Unicode，`n` 是 bytes |
| `nvarchar(n)` | `string` | UTF-16 byte-pair；C# `string` 本身不標示 varchar / nvarchar |
| `uniqueidentifier` | `Guid` | SQL NULL 時為 `Guid?` |
| `datetime` | `DateTime` | 精度與範圍比 `datetime2` 舊且窄 |
| `datetime2` | `DateTime` | 沒有 offset / timezone |
| `datetimeoffset` | `DateTimeOffset` | 保存 offset，仍不等於完整 time zone |
| `decimal(p,s)` | `decimal` | precision / scale 要另外設定 |
| `varbinary(n/max)` | `byte[]`（EF Core） | 大檔案串流要在 ADO.NET 用 `SqlDataReader.GetStream()` + `SequentialAccess` |

## 3. 查詢結果（預期輸出）

```sql
SELECT
    CAST('A' AS char(4))       AS FixedText,
    CAST('A' AS varchar(4))    AS VariableText,
    DATALENGTH(CAST('A' AS char(4)))    AS FixedBytes,
    DATALENGTH(CAST('A' AS varchar(4))) AS VariableBytes;
```

預期結果（需在指定 SQL Server build 執行後核對）：

```text
FixedText  VariableText  FixedBytes  VariableBytes
A          A             4           1
```

`char(4)` 固定使用 4 bytes；`varchar(4)` 的 `4` 是 bytes 上限，實際值只有 1 byte。長度固定的代碼欄位才用 `char`；一般名稱、email、URL 依 Unicode 與 collation 需求選擇可變長度型別。

```sql
SELECT
    CAST('2026-09-08 10:20:30.1234567' AS datetime2(7)) AS LocalLikeTime,
    CAST('2026-09-08 10:20:30.1234567 +08:00' AS datetimeoffset(7)) AS WithOffset;
```

預期結果：

```text
LocalLikeTime                  WithOffset
2026-09-08 10:20:30.1234567    2026-09-08 10:20:30.1234567 +08:00
```

`datetime2` 只保存日期與時間；`datetimeoffset` 額外保存 `+08:00` 這類 offset。`datetime` 的小數秒只會落在 `.000`、`.003`、`.007`；`23:59:59.999` 會捨入成隔天 `00:00:00.000`。`datetime2(7)` 精度是 100 nanoseconds；`datetime`、`datetime2`、`datetimeoffset` 的儲存大小也會依型別與 scale 不同。

## 4. SQL Server 背後大概做什麼

### `varchar` vs `nvarchar`

- `varchar` 依 collation 決定編碼；傳統定序受 code page 限制，SQL Server 2019+ 使用 `_UTF8` 定序時可存完整 Unicode，但 `varchar(n)` 的 `n` 是 bytes。UTF-8 定序下中文一字約 3 bytes，`varchar(20)` 不能當成可放 20 個中文字。
- `nvarchar` 使用 UTF-16 byte-pair；增補字元（例如部分 emoji）可能佔兩個 byte-pair。跨系統資料若沒有明確 UTF-8 contract，使用 `nvarchar` 通常較直接。
- SQL literal 使用 `N'中文'` 才明確表示 Unicode：

```sql
SELECT * FROM Orders WHERE CustomerName = N'小明';
```

不要把「所有欄位都用 `nvarchar(max)`」當成 Unicode 最佳實務；長度、索引能力、row size、memory 與資料品質仍需設計。

### `char` vs `varchar`

- `char(n)` 固定長度，適合長度固定的國碼、狀態碼、hash representation 等少數場景。
- `varchar(n)` 可變長度，適合長度不固定的短字串。
- `char` 的尾端空白、比較與應用程式 trim 行為要特別測試。

### `datetime`、`datetime2`、`datetimeoffset`

- `datetime` 是 legacy type，範圍從 1753、精度較低；新 schema 通常優先 `datetime2`。
- `datetime2` 有較好的範圍與精度，但沒有 offset；語意通常是 UTC datetime 或 local wall-clock，要由 schema / application contract 定義。
- `datetimeoffset` 保存日期時間與 offset，適合 API event、audit、跨時區傳輸；它不保存 `Asia/Taipei` 這種 time-zone rules。

### `decimal(p,s)`

`p` 是總有效位數，`s` 是小數位數。`decimal(19,4)` 最多 15 位整數 + 4 位小數，不是「19 位整數」。金額、匯率、計算結果優先使用 `decimal`；`float`／`real` 是近似值，不要拿來做需要精確結果的金額或 `=` 比較。`text`／`ntext`／`image` 已宣告將移除，新開發改用 `(n)varchar(max)`／`varbinary(max)`。

### `uniqueidentifier`

它是 16-byte GUID，適合跨服務產生的識別碼。隨機 GUID 當 clustered key 會造成隨機插入與 page split；EF Core SQL Server provider 對 `Guid` 主鍵預設產生循序 GUID。自己指定 `Guid.NewGuid()` 或 SQL Server `NEWID()` 才會改成隨機值；若要對外不可猜測，可把內部循序 key 與 public ID 分開。

### NULL

`NULL` 表示未知或不存在的值，不是空字串、0、`false`、`Guid.Empty` 或 `DateTime.MinValue`。下一章會專門處理三值邏輯。

## 5. 與 C# / EF Core 的關聯

```csharp
public sealed class Order
{
    public Guid OrderId { get; set; }
    public required string CustomerName { get; set; }
    public decimal Amount { get; set; }
    public bool IsPaid { get; set; }
    public DateTime OrderedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}
```

EF Core model configuration：

```csharp
modelBuilder.Entity<Order>(entity =>
{
    entity.Property(x => x.CustomerName)
        .HasMaxLength(200)
        .IsUnicode(true); // nvarchar(200)

    entity.Property(x => x.Amount)
        .HasPrecision(19, 4);

    entity.Property(x => x.PaidAt)
        .HasColumnType("datetimeoffset(7)");
});
```

`string` 預設對應 `nvarchar(max)`；要 `varchar` 必須用 `.IsUnicode(false)` 或 `HasColumnType`，然後產生 migration。C# nullable `DateTimeOffset?` 對應可 NULL 的 `datetimeoffset`，但資料庫既有 schema 仍需要 migration 與驗證。EF Core SQL Server provider 的 `decimal` 預設是 `decimal(18,2)`，金額或匯率應明確設定 precision，否則 migration 會警告值可能被截斷。

其他常見預設對應是 `DateTime` → `datetime2(7)`、`DateOnly` → `date`、`TimeOnly` → `time`、`byte[]` → `varbinary(max)`；migration 產出的 column type 才是最後要驗證的 schema。

## 6. 常見誤區

- C# `DateTime.MinValue` 是西元 1 年；寫入 SQL `datetime`（起始年份 1753）會失敗，schema 與 application 的範圍要一起驗證。
- SQL `NULL` 不要在應用層偷換成 0、空字串或 `Guid.Empty`，除非領域明確定義那個特殊值。
- SQL Server migration 對未設定 precision 的 `decimal` 可能使用 `decimal(18,2)`；金額與匯率要在 Fluent API 明確設定。

## 7. 面試回答

> SQL Server type 是 schema contract，不是 C# type 的一對一鏡像。新 schema 通常用 `nvarchar` 處理 Unicode、`datetime2` 取代 legacy `datetime`、需要 offset 時用 `datetimeoffset`，金額用 `decimal(p,s)`。`NULL` 代表未知或不存在，SQL 判斷會進入三值邏輯。EF Core mapping 仍需設定 Unicode、precision、column type 與 nullable，而不是只看 C# property type。

## 8. 小練習

1. 為「付款金額、付款發生時間、使用者名稱」選 SQL Server type。
2. 解釋 `decimal(19,4)` 能表示多少位整數與小數。
3. 為什麼隨機 `uniqueidentifier` 不一定適合當 clustered index key？
