---
title: 18 SQL Server 資料型別
tags: [sql-server, mssql, data-types, csharp, ef-core]
---

# 18 SQL Server 資料型別

## 學習目標

- 從 application / EF Core 邊界理解 SQL Server type，而不是只背名稱。
- 能在 `varchar`、`nvarchar`、`datetime2`、`datetimeoffset`、`decimal(p,s)` 之間做合理選擇。
- 知道 SQL Server type 與 C# type 不是一對一轉換。

## 1. 一句話理解

資料型別是資料庫的 contract：它決定可儲存的值、比較與排序方式、精度、索引大小、NULL 行為與 ORM mapping；C# 的 `string` 或 `DateTime` 不會替你完成這些決策。

## 2. 實際 SQL

```sql
CREATE TABLE Users
(
    Id              uniqueidentifier NOT NULL,
    DisplayName     nvarchar(200) NOT NULL,
    LegacyCode      varchar(20) NULL,
    ShortLabel      char(8) NULL,
    Balance         decimal(19, 4) NOT NULL,
    IsActive        bit NOT NULL,
    CreatedAt       datetime2(7) NOT NULL,
    OccurredAt      datetimeoffset(7) NULL,
    Avatar          varbinary(max) NULL,
    CONSTRAINT PK_Users PRIMARY KEY (Id)
);
```

### SQL Server ↔ C# 常見對照

| SQL Server | C# 常見對應 | 重要提醒 |
| --- | --- | --- |
| `int` | `int` | 32-bit signed integer |
| `bigint` | `long` | 64-bit signed integer |
| `bit` | `bool` | SQL `NULL` 時 C# 通常是 `bool?` |
| `varchar(n)` | `string` | 非 Unicode；實際 encoding / collation 由 DB 設定 |
| `nvarchar(n)` | `string` | Unicode；C# `string` 本身不標示 varchar / nvarchar |
| `uniqueidentifier` | `Guid` | SQL NULL 時為 `Guid?` |
| `datetime` | `DateTime` | 精度與範圍比 `datetime2` 舊且窄 |
| `datetime2` | `DateTime` | 沒有 offset / timezone |
| `datetimeoffset` | `DateTimeOffset` | 保存 offset，仍不等於完整 time zone |
| `decimal(p,s)` | `decimal` | precision / scale 要另外設定 |
| `varbinary(n/max)` | `byte[]` / `Stream` | 大 payload 不一定適合一次載入 byte[] |

## 3. 執行結果

```sql
SELECT
    CAST('A' AS char(4))       AS FixedText,
    CAST('A' AS varchar(4))    AS VariableText,
    DATALENGTH(CAST('A' AS char(4)))    AS FixedBytes,
    DATALENGTH(CAST('A' AS varchar(4))) AS VariableBytes;
```

概念結果：`char(4)` 會以固定長度儲存，`varchar(4)` 只儲存實際長度。固定長度對真正固定寬度的 code / flag 有意義；一般名稱、email、URL 多使用 `varchar` / `nvarchar`。

```sql
SELECT
    CAST('2026-09-08 10:20:30.1234567' AS datetime2(7)) AS LocalLikeTime,
    CAST('2026-09-08 10:20:30.1234567 +08:00' AS datetimeoffset(7)) AS WithOffset;
```

`datetime2` 只保存日期與時間；`datetimeoffset` 額外保存 `+08:00` 這類 offset。SQL Server 的 `datetime` 精度約為 3.33 milliseconds；`datetime2` 可到 100 nanoseconds 的 precision（依 scale）。

## 4. SQL Server 背後大概做什麼

### `varchar` vs `nvarchar`

- `varchar` 是非 Unicode 字串，容量與 collation / code page 有關。
- `nvarchar` 是 Unicode 字串，中文、多語言、跨系統資料通常較安全。
- SQL literal 使用 `N'中文'` 才明確表示 Unicode：

```sql
SELECT * FROM Users WHERE DisplayName = N'小明';
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

`p` 是總有效位數，`s` 是小數位數。`decimal(19,4)` 最多 15 位整數 + 4 位小數，不是「19 位整數」。金額、匯率、計算結果優先使用 `decimal`，不要用 `float` / `real` 期待精確金額。

### `uniqueidentifier`

它是 16-byte GUID。適合 distributed ID、外部不可猜測的 identifier，但隨機 GUID 當 clustered key 可能造成 page split / fragmentation；要依 workload 考慮 sequential ID、clustered key 與 public ID 是否分離。

### NULL

`NULL` 表示未知或不存在的值，不是空字串、0、`false`、`Guid.Empty` 或 `DateTime.MinValue`。下一章會專門處理三值邏輯。

## 5. 與 C# / EF Core 的關聯

```csharp
public sealed class User
{
    public Guid Id { get; set; }
    public required string DisplayName { get; set; }
    public decimal Balance { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
}
```

EF Core model configuration：

```csharp
modelBuilder.Entity<User>(entity =>
{
    entity.Property(x => x.DisplayName)
        .HasMaxLength(200)
        .IsUnicode(true); // nvarchar(200)

    entity.Property(x => x.Balance)
        .HasPrecision(19, 4);

    entity.Property(x => x.OccurredAt)
        .HasColumnType("datetimeoffset(7)");
});
```

C# `string` 不會自動告訴 EF Core 你要 `varchar` 還是 `nvarchar`；provider convention、`IsUnicode`、column type 與 migration 決定實際 schema。C# nullable `DateTimeOffset?` 對應可 NULL 的 `datetimeoffset`，但資料庫既有 schema 仍需要 migration 與驗證。

## 6. 常見誤區

- `nvarchar` 不是「比較安全所以永遠全部使用」；索引 key size、容量與 collation 仍重要。
- `datetime2` 不帶 timezone；它不像 C# `DateTimeOffset`。
- `decimal(10,2)` 不是 10 位整數 + 2 位小數，而是總共 10 位有效數字。
- `uniqueidentifier` 不代表天然適合 clustered index；寫入順序與 page locality 仍要考慮。
- C# `DateTime`、SQL `datetime`、SQL `datetime2` 的精度與範圍並不完全相同。
- `NULL` 不要在 application layer 偷換成 magic value，除非 domain 明確定義那個值。

## 7. 面試回答

> SQL Server type 是 schema contract，不是 C# type 的一對一鏡像。新 schema 通常用 `nvarchar` 處理 Unicode、`datetime2` 取代 legacy `datetime`、需要 offset 時用 `datetimeoffset`，金額用 `decimal(p,s)`。`NULL` 代表未知或不存在，SQL 判斷會進入三值邏輯。EF Core mapping 仍需設定 Unicode、precision、column type 與 nullable，而不是只看 C# property type。

## 8. 小練習

1. 為「付款金額、付款發生時間、使用者名稱」選 SQL Server type。
2. 解釋 `decimal(19,4)` 能表示多少位整數與小數。
3. 為什麼隨機 `uniqueidentifier` 不一定適合當 clustered index key？
