---
title: 19 SQL Server NULL 與三值邏輯
tags: [sql-server, mssql, null, logic, ef-core]
---

# 19 SQL Server NULL 與三值邏輯

## 學習目標

- 能準確解釋 `NULL`、`UNKNOWN` 與 `WHERE` 的關係。
- 正確使用 `IS NULL`、`ISNULL`、`COALESCE`、`NULLIF`。
- 看懂 NULL 如何影響 JOIN、`NOT IN` 與 C# nullable。

## 1. 一句話理解

SQL 的 `NULL` 是「未知／不存在」的標記，不能用 `=` 比較；在 `ANSI_NULLS ON` 下，與 NULL 常值或變數的一般比較得到 `UNKNOWN`，而 `WHERE` 只保留 `TRUE`。

## 2. 實際 SQL

```sql
DECLARE @x int = NULL;

SELECT
    CASE WHEN @x = NULL THEN 'TRUE' ELSE 'not true' END AS EqualsNull,
    CASE WHEN @x IS NULL THEN 'TRUE' ELSE 'FALSE' END AS IsNullCheck;
```

預期結果（`ANSI_NULLS ON`，需在 SQL Server 執行後核對）：

```text
EqualsNull  IsNullCheck
not true    TRUE
```

正確篩選：

```sql
SELECT * FROM Users WHERE PhoneNumber IS NULL;
SELECT * FROM Users WHERE PhoneNumber IS NOT NULL;
```

錯誤或不符合預期：

```sql
SELECT * FROM Users WHERE PhoneNumber = NULL;
SELECT * FROM Users WHERE PhoneNumber <> NULL;
```

## 3. 執行結果

SQL boolean expression 不是只有 true / false：

| 表達式 | 結果 |
| --- | --- |
| `1 = 1` | `TRUE` |
| `1 = 2` | `FALSE` |
| `1 = NULL` | `UNKNOWN` |
| `NULL = NULL` | `UNKNOWN` |
| `NULL <> 1` | `UNKNOWN` |
| `NULL IS NULL` | `TRUE` |
| `NULL IS NOT DISTINCT FROM NULL`（SQL Server 2022+） | `TRUE` |
| `1 IS DISTINCT FROM NULL`（SQL Server 2022+） | `TRUE` |

```sql
SELECT *
FROM Users
WHERE PhoneNumber = NULL;
```

在 `ANSI_NULLS ON` 下結果是 0 rows，因為 `WHERE UNKNOWN` 不會被保留。`ANSI_NULLS OFF` 已淘汰，且只影響欄位和 NULL 常值／變數的比較；連接時驅動程式會自動使用 `ON`。

## 4. SQL Server 背後大概做什麼

### `ISNULL`、`COALESCE`、`NULLIF`

```sql
SELECT
    ISNULL(PhoneNumber, 'not provided') AS Phone1,
    COALESCE(PhoneNumber, Email, 'not provided') AS Contact,
    NULLIF(TRIM(DisplayName), '') AS EmptyToNull
FROM Users;
```

- `ISNULL(expression, replacement)`：SQL Server 函式，只接受兩個參數；回傳型別就是第一個參數型別（第一個是 literal `NULL` 時例外）。
- `COALESCE(a, b, c)`：回傳第一個非 NULL 值；標準 SQL 運算式，可接受多個參數；型別依資料型別優先權決定。
- `NULLIF(a, b)`：若 `a = b` 回傳 NULL，否則回傳 `a`；常把空字串、0 等「不應算入」值轉成 NULL。

```sql
SELECT
    ISNULL(CAST(NULL AS varchar(3)), 'long text') AS IsNullResult,
    COALESCE(CAST(NULL AS varchar(3)), 'long text') AS CoalesceResult;
```

預期結果（需在 SQL Server 執行後核對）：

```text
IsNullResult  CoalesceResult
lon           long text
```

`ISNULL` 的回傳型別等於第一個參數；第一個參數是 literal `NULL` 時才採第二個參數型別，第二個值會先轉成第一個型別，所以 `long text` 被截成 `lon`。`COALESCE` 依資料型別優先權決定型別，結果可保留完整字串；它含子查詢時可能改寫成 `CASE` 而評估輸入多次。

### 聚合與字串串接

除了 `COUNT(*)`，聚合函式會忽略 NULL；`COUNT(column)` 只數非 NULL 值：

```sql
SELECT
    COUNT(*) AS AllRows,
    COUNT(PhoneNumber) AS RowsWithPhone,
    AVG(CAST(CASE WHEN PhoneNumber IS NULL THEN 0 ELSE 1 END AS decimal(9,2))) AS PhoneRate
FROM Users;
```

`'abc' + NULL` 在 `CONCAT_NULL_YIELDS_NULL ON` 下得到 NULL；要保留其他片段可用 `CONCAT('abc', NULL)` 得到 `'abc'`，或明確使用 `COALESCE`。`CONCAT_NULL_YIELDS_NULL OFF` 已淘汰。

### NULL 與 `NOT IN`

```sql
-- 如果 BlockedUserIds.UserId 其中一筆是 NULL，NOT IN 可能得到 UNKNOWN，導致沒有預期資料。
SELECT *
FROM Users u
WHERE u.Id NOT IN (SELECT UserId FROM BlockedUserIds);

-- 對可含 NULL 的子查詢，使用 `NOT EXISTS` 明確表達「不存在匹配」：
SELECT *
FROM Users u
WHERE NOT EXISTS
(
    SELECT 1
    FROM BlockedUserIds b
    WHERE b.UserId = u.Id
);
```

EF Core 對應查詢：

```csharp
var blockedIds = db.BlockedUserIds.Select(x => x.UserId);
var allowedUsers = db.Users
    .Where(user => !blockedIds.Contains(user.Id));
Console.WriteLine(allowedUsers.ToQueryString());
```

預設 null compensation 會把它翻成 `NOT EXISTS`；`UseRelationalNulls()` 才可能保留 `NOT IN`。`LEFT JOIN` 右側沒有匹配時也會產生 NULL，若在 `WHERE` 再篩右表欄位，常會把外連接效果排掉，詳見 [[20-SQLServer-JOIN]]。

## 5. 與 C# / EF Core 的關聯

```csharp
public sealed class User
{
    public string? PhoneNumber { get; set; }
    public string? Email { get; set; }
}

var usersWithoutPhone = await db.Users
    .Where(x => x.PhoneNumber == null)
    .ToListAsync(cancellationToken);
```

EF Core 預設會把 C# 的 null 比較語意補償成 SQL：`x.PhoneNumber == null` 翻成 `IS NULL`；兩個可為 NULL 的欄位互比時，會加入 `IS NULL`／`IS NOT NULL` 條件，讓結果接近 C# 的 two-valued logic。`UseRelationalNulls()` 才會改用資料庫原生的三值邏輯；這會改變 LINQ 查詢的意義，使用前要有明確理由。

```csharp
var query = db.Users.Where(x => x.PhoneNumber != x.Email);
Console.WriteLine(query.ToQueryString());
```

預設 SQL（EF Core 10 + SQL Server provider 的 `ToQueryString()`）：

```sql
WHERE ([u].[PhoneNumber] <> [u].[Email] OR [u].[PhoneNumber] IS NULL OR [u].[Email] IS NULL)
  AND ([u].[PhoneNumber] IS NOT NULL OR [u].[Email] IS NOT NULL)
```

若在 options 中呼叫 `UseRelationalNulls()`，SQL 會改成：

```sql
WHERE [u].[PhoneNumber] <> [u].[Email]
```

`string?` 是 C# 編譯期的可為 null 註記；資料庫 `NULL` 是執行期資料值。啟用 NRT 時，EF Core 通常把 `string?` 對應成可 NULL 欄位、`string` 對應成 NOT NULL 欄位，migration 仍要核對既有 schema。

## 6. 常見誤區

- `column = NULL` 和 `column IS NULL` 完全不同。
- `NULL` 不等於空字串，`0`，`false` 或 `Guid.Empty`。
- `COALESCE` 不一定和 `ISNULL` 有相同 result type / nullability。
- `NOT IN` 遇到子查詢 NULL 可能讓結果全空；檢查 `NOT EXISTS` 是否更符合語意。
- `COUNT(column)` 不計 NULL，`COUNT(*)` 計所有列；字串用 `+` 串接時任一 NULL 可能讓整串變 NULL，需用 `CONCAT` 或 `COALESCE`。
- SQL Server 2022+ 可用 `IS [NOT] DISTINCT FROM`，把 NULL 當已知值比較並保證回傳 TRUE／FALSE。
- `LEFT JOIN` 右側沒有匹配時會製造 NULL，下一章的 `WHERE right_table.column = ...` 可能又把它排掉。

## 7. 面試回答

> 在 `ANSI_NULLS ON` 下，SQL Server 使用三值邏輯：TRUE、FALSE、UNKNOWN。`NULL` 代表未知或不存在，所以 `column = NULL` 與 `column <> NULL` 都是 UNKNOWN，`WHERE` 只保留 TRUE，必須用 `IS NULL`／`IS NOT NULL`。`ISNULL` 是 SQL Server 的兩參數函式，`COALESCE` 是可多參數的標準 SQL 運算式；兩者的型別與可為 NULL 判定不同。`NULLIF` 常用來把特定特殊值轉成 NULL。

## 8. 小練習

1. 修正 `WHERE DeletedAt = NULL`。
2. 用 `NULLIF` 將空字串 `''` 轉成 NULL。
3. 解釋為什麼含 NULL 的 `NOT IN` 可能和直覺不同。
