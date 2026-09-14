---
title: 27 SQL Server Stored Procedure Temporary Table Pagination
tags: [sql-server, mssql, stored-procedure, temp-table, pagination]
---

# 27 Stored Procedure、Function、Trigger、Temp Table 與 Pagination

## 學習目標

- 知道 DB logic 與 application logic 的責任分界。
- 分辨 `#TempTable`、`@TableVariable`、CTE 的生命週期與使用情境。
- 能為大量資料設計比 OFFSET 更穩定的 keyset pagination。

## 1. 一句話理解

SQL Server programmable objects 與中間結果工具各自有用途；pagination 則是在資料量大時選擇「跳過前面 rows」或「從上一頁 key 繼續」的 query contract。

## 2. 實際 SQL

### Stored procedure / function / trigger

```sql
CREATE OR ALTER PROCEDURE dbo.GetActiveUsers
    @PageSize int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@PageSize) Id, DisplayName
    FROM dbo.Users
    WHERE IsActive = 1
    ORDER BY CreatedAt DESC, Id DESC;
END;
```

```sql
CREATE OR ALTER FUNCTION dbo.GetUserOrderCount(@UserId int)
RETURNS int
AS
BEGIN
    DECLARE @count int;
    SELECT @count = COUNT(*) FROM Orders WHERE UserId = @UserId;
    RETURN @count;
END;
```

```sql
CREATE OR ALTER FUNCTION dbo.GetOrdersForUser(@UserId int)
RETURNS TABLE
AS
RETURN
(
    SELECT Id, UserId, Status, TotalAmount, CreatedAt
    FROM Orders
    WHERE UserId = @UserId
);

DECLARE @UserId int = 42;
SELECT *
FROM dbo.GetOrdersForUser(@UserId);
```

```sql
CREATE OR ALTER TRIGGER dbo.OrderAuditTrigger
ON dbo.Orders
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF (ROWCOUNT_BIG() = 0) RETURN;
    INSERT INTO OrderAudit(OrderId, ChangedAt)
    SELECT Id, SYSUTCDATETIME()
    FROM inserted;
END;
```

Trigger 必須以 set-based 思維處理 `inserted` / `deleted`，因為一次 statement 可能影響多列，不要把 trigger 寫成只處理一列。

### Temp table、table variable、CTE

```sql
CREATE TABLE #RecentOrders
(
    OrderId int NOT NULL,
    UserId int NOT NULL,
    CreatedAt datetime2 NOT NULL
);

CREATE INDEX IX_RecentOrders_UserId ON #RecentOrders(UserId);

DECLARE @SmallResult TABLE
(
    Id int PRIMARY KEY,
    Name nvarchar(100)
);

WITH Recent AS
(
    SELECT TOP (100) *
    FROM Orders
    ORDER BY CreatedAt DESC, Id DESC
)
SELECT * FROM Recent;
```

## 3. 執行結果

### 物件比較

| 工具 | Scope / lifetime | Statistics / index 直覺 | 適合 |
| --- | --- | --- | --- |
| `#TempTable` | current session；stored procedure 結束時自動 drop，巢狀 procedure 看得到，呼叫者看不到 | 可建立 index，有 statistics | 較大量中間結果與多次 reuse |
| `@TableVariable` | batch / procedure scope；transaction rollback 不會回滾其內容 | 沒有 column statistics；2019 compat 150+ 用實際列數 deferred compilation | 小量、短 scope 結果 |
| CTE | 緊接一個 statement | 不保存、不跨 statement、不是自動 materialize | 單一 query 的可讀性、recursive query、window 分層 |

### OFFSET / FETCH

```sql
SELECT Id, Name, CreatedAt
FROM Users
ORDER BY CreatedAt DESC, Id DESC
OFFSET @Offset ROWS
FETCH NEXT @PageSize ROWS ONLY;
```

page 1 只需跳過少量 rows；page 10000 仍可能需要找到並丟棄大量前置 rows。即使有 index，也可能因深 offset 增加 I/O 與 sort / lookup 成本。

`OFFSET` 必須接在 `ORDER BY` 後，`FETCH` 必須搭配 `OFFSET`；SQL Server 2012+ 支援，且同一 query expression 不能同時使用 `TOP`。翻頁排序要包含唯一 key，否則結果順序不穩定。

### Keyset pagination

```sql
-- 上一頁最後一筆的 cursor：@lastCreatedAt、@lastId
SELECT TOP (@PageSize)
    Id, Name, CreatedAt
FROM Users
WHERE CreatedAt < @lastCreatedAt
   OR (CreatedAt = @lastCreatedAt AND Id < @lastId)
ORDER BY CreatedAt DESC, Id DESC;
```

對應 index：

```sql
CREATE INDEX IX_Users_CreatedAt_Id
ON Users(CreatedAt DESC, Id DESC)
INCLUDE(Name);
```

Keyset 的前提是排序 key 穩定且 cursor 能表達嚴格順序；`CreatedAt` 可能相同，所以要加 unique tie-breaker，如 `Id`。

EF Core keyset query 的 `ToQueryString()`（固定 `pageSize = 20`、cursor `CreatedAt = 2026-01-01`、`Id = 100`）會包含：

```sql
SELECT TOP(@p) [u].[Id], [u].[CreatedAt], [u].[Name]
FROM [Users] AS [u]
WHERE [u].[CreatedAt] < @cursor_CreatedAt
   OR ([u].[CreatedAt] = @cursor_CreatedAt AND [u].[Id] < @cursor_Id)
ORDER BY [u].[CreatedAt] DESC, [u].[Id] DESC
```

## 4. SQL Server 背後大概做什麼

### DB layer vs application layer

| Object | 回傳 / 觸發方式 | 常見用途 | 需要小心 |
| --- | --- | --- | --- |
| Stored procedure | `EXEC`，可回多個 result sets / output parameters | command、交易內多步驟操作、受控資料存取 | contract、版本、測試與 deployment |
| Scalar function | 每次回一個 scalar value | 純計算、可組合 expression 的小型邏輯 | SQL Server 2019 compat 150+ 符合條件者可 scalar UDF inlining；更早版本逐筆呼叫、不計成本且禁止 query parallelism |
| Table-valued function | 回傳 table-shaped row set，可放在 FROM | reusable parameterized read query | inline TVF 與 multi-statement TVF 的 plan / cardinality 差異 |
| Trigger | DML / DDL event 後自動執行 | 強制 audit、跨 row invariant 的 DB-side reaction | 隱藏副作用、transaction 變長、multi-row handling、debugging |

適合放 DB 的例子：

- 一定要和資料寫入同一 transaction 的 constraint / trigger / audit。
- 高度 set-based 的 reporting query、已被多個應用共用且邊界明確的 read model。
- 權限受控、需要由 DBA 審查的 stored procedure。

適合放 application layer 的例子：

- 需要外部 API、複雜 domain orchestration、可測試的 use case。
- 與 transport / HTTP / UI 強耦合的邏輯。
- 不應被藏在 trigger 中的跨表副作用。

預設把複雜 domain orchestration 留在 application layer；只有需要和寫入同一 transaction、或 DBA 必須審查的 set-based／權限邊界，才把邏輯放進 DB。最後仍要用維護性、權限、transaction、重用、部署與效能分析驗證。

### Pagination 的一致性

如果排序欄位在翻頁間被修改，OFFSET 或 keyset 都可能出現重複 / 漏資料；cursor contract 要定義 snapshot / ordering / eventual consistency。對需要一致報表的場景，可能要 transaction / snapshot 或固定查詢時間點，但這會增加成本。

## 5. 與 C# / EF Core 的關聯

EF Core keyset pagination：

```csharp
IQueryable<User> query = db.Users
    .OrderByDescending(x => x.CreatedAt)
    .ThenByDescending(x => x.Id);

if (cursor is not null)
{
    query = query.Where(x =>
        x.CreatedAt < cursor.CreatedAt ||
        (x.CreatedAt == cursor.CreatedAt && x.Id < cursor.Id));
}

var page = await query
    .Take(pageSize)
    .Select(x => new UserListItem(x.Id, x.Name, x.CreatedAt))
    .ToListAsync(cancellationToken);
```

`IQueryable` 要保持到 `Take`／投影之後；太早 `ToList()` 會把 pagination 搬到記憶體。Stored procedure 可用 `FromSql`／ADO.NET，但要使用參數化 API，不要串接 user input。`FromSql` 只能直接接在 `DbSet` 上；SQL Server 不允許對 `EXEC` 再接 `Where`／`OrderBy`，否則會丟：

```text
InvalidOperationException: 'FromSql' or 'SqlQuery' was called with non-composable SQL and with a query composing over it. Consider calling 'AsEnumerable' after the method to perform the composition on the client side.
```

`FromSql` 要回傳 entity 的全部欄位；非 entity 結果可用 EF Core 7+ 的 `Database.SqlQuery<T>`，需要明確型別／長度時傳 `SqlParameter`。SQL Server 2019 compat 150+ 可查 `sys.sql_modules.is_inlineable` 判斷 scalar UDF 是否可 inlining。

## 6. 常見誤區

- table variable 不是永遠「在 memory」，temp table 也不是永遠「磁碟」；實際配置與 tempdb 由 engine 管理。
- CTE 不等於 temp table，不提供跨 statement reuse 或自動 statistics。
- trigger 一次 statement 可能影響多列，不能只 `SELECT TOP 1`。
- OFFSET 適合小到中型頁數或簡單 admin screen，不是大型 feed 的萬用 pagination。
- keyset cursor 必須包含完整、穩定、可比較的排序 key。

## 7. 面試回答

> `#TempTable` 適合較大量、需要 index / statistics 或多次 reuse 的中間結果；table variable 通常適合小量、短 scope 資料；CTE 是單一 statement 的命名 query expression，不是暫存 table。OFFSET/FETCH 在深頁數可能越來越慢，feed / 大資料量通常考慮 keyset pagination，使用上一頁最後一筆的排序 key 繼續查，並確保排序 key 唯一且有合適 index。

## 8. 小練習

1. 將 OFFSET pagination 改成 keyset pagination。
2. 何時選 `#TempTable` 而不是 CTE？
3. 為 order audit trigger 說明如何處理多筆 inserted rows。
