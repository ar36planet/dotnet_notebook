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

SQL 的 `NULL` 不是一個可以用 `=` 比較的值，而是「未知／不存在」的標記；任何與 NULL 的一般比較通常得到 `UNKNOWN`，而 `WHERE` 只保留 `TRUE`。

## 2. 實際 SQL

```sql
DECLARE @x int = NULL;

SELECT
    CASE WHEN @x = NULL THEN 'TRUE' ELSE 'not true' END AS EqualsNull,
    CASE WHEN @x IS NULL THEN 'TRUE' ELSE 'FALSE' END AS IsNullCheck;
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

```sql
SELECT *
FROM Users
WHERE PhoneNumber = NULL;
```

結果通常是 0 rows，因為 `WHERE UNKNOWN` 不會被保留；它不是把 UNKNOWN 當成 TRUE，也不是把 NULL 視為某個特殊字串。

## 4. SQL Server 背後大概做什麼

### `ISNULL`、`COALESCE`、`NULLIF`

```sql
SELECT
    ISNULL(PhoneNumber, 'not provided') AS Phone1,
    COALESCE(PhoneNumber, Email, 'not provided') AS Contact,
    NULLIF(TRIM(DisplayName), '') AS EmptyToNull
FROM Users;
```

- `ISNULL(expression, replacement)`：SQL Server function，只接受兩個參數；回傳型別主要採第一個參數的型別。
- `COALESCE(a, b, c)`：回傳第一個非 NULL 值；標準 SQL 語意，可接受多個參數；型別依 data type precedence 決定。
- `NULLIF(a, b)`：若 `a = b` 回傳 NULL，否則回傳 `a`；常把空字串、0 等「不應算入」值轉成 NULL。

```sql
SELECT
    ISNULL(CAST(NULL AS varchar(3)), 'long text') AS IsNullResult,
    COALESCE(CAST(NULL AS varchar(3)), 'long text') AS CoalesceResult;
```

兩者在型別長度、nullability inference 與 expression evaluation 上可能不同，不要只因結果看起來一樣就視為完全等價。

### NULL 與 `NOT IN`

```sql
-- 如果 BlockedUserIds.UserId 其中一筆是 NULL，NOT IN 可能得到 UNKNOWN，導致沒有預期資料。
SELECT *
FROM Users u
WHERE u.Id NOT IN (SELECT UserId FROM BlockedUserIds);

-- 對可含 NULL 的子查詢，通常更穩妥地表達「不存在匹配」：
SELECT *
FROM Users u
WHERE NOT EXISTS
(
    SELECT 1
    FROM BlockedUserIds b
    WHERE b.UserId = u.Id
);
```

## 5. 與 C# / EF Core 的關聯

```csharp
public sealed class User
{
    public string? PhoneNumber { get; set; }
}

var usersWithoutPhone = await db.Users
    .Where(x => x.PhoneNumber == null)
    .ToListAsync(cancellationToken);
```

EF Core 會針對 nullable comparison 產生符合 SQL null semantics 的查詢，實際 SQL 仍應透過 logging / `ToQueryString()` 驗證：

```csharp
var query = db.Users.Where(x => x.PhoneNumber == null);
Console.WriteLine(query.ToQueryString());
```

`string?` 是 C# compiler nullability 意圖；資料庫 `NULL` 是 runtime data value。兩者相關但不是同一層的功能。

## 6. 常見誤區

- `column = NULL` 和 `column IS NULL` 完全不同。
- `NULL` 不等於空字串，`0`，`false` 或 `Guid.Empty`。
- `COALESCE` 不一定和 `ISNULL` 有相同 result type / nullability。
- `NOT IN` 遇到子查詢 NULL 可能讓結果全空；檢查 `NOT EXISTS` 是否更符合語意。
- `LEFT JOIN` 右側沒有匹配時會製造 NULL，下一章的 `WHERE right_table.column = ...` 可能又把它排掉。

## 7. 面試回答

> SQL Server 使用三值邏輯：TRUE、FALSE、UNKNOWN。`NULL` 代表未知或不存在，所以 `column = NULL` 與 `column <> NULL` 都通常是 UNKNOWN，`WHERE` 只保留 TRUE，必須用 `IS NULL` / `IS NOT NULL`。`ISNULL` 是 SQL Server 的兩參數 function，`COALESCE` 是可多參數的標準式子，型別與 nullability 行為可能不同；`NULLIF` 常用來把特定 magic value 轉成 NULL。

## 8. 小練習

1. 修正 `WHERE DeletedAt = NULL`。
2. 用 `NULLIF` 將空字串 `''` 轉成 NULL。
3. 解釋為什麼含 NULL 的 `NOT IN` 可能和直覺不同。
