---
title: 21 SQL Server Subquery CTE View
tags: [sql-server, mssql, cte, subquery, view]
---

# 21 Subquery、CTE、View 與 Derived Table

## 學習目標

- 區分 scalar / correlated subquery、derived table、CTE 與 view。
- 理解 CTE 是查詢表達方式，不是自動 materialize 的暫存 table。
- 能選擇可讀性、reuse、可測試性與效能較合理的寫法。

## 1. 一句話理解

Subquery、derived table、CTE 與 view 都是在組合 query；它們主要差在 scope、命名、可重用性與 schema contract，不代表其中某一種天生比較快。

## 2. 實際 SQL

### Scalar subquery

```sql
SELECT
    u.Id,
    u.Name,
    (SELECT COUNT(*)
     FROM Orders o
     WHERE o.UserId = u.Id) AS OrderCount
FROM Users u;
```

scalar subquery 必須回傳一列；若改成直接取 `o.Amount`，一位 user 有兩筆 order 時會失敗：

```sql
-- 這段在 Ada 有兩筆 order 時會丟錯
SELECT u.Id,
       (SELECT o.Amount FROM Orders o WHERE o.UserId = u.Id) AS Amount
FROM Users u;
```

預期錯誤（需在 SQL Server 執行後核對）：

```text
Msg 512, Level 16: Subquery returned more than 1 value. This is not permitted when the subquery follows =, !=, <, <=, >, >= or when the subquery is used as an expression.
```

### Derived table

```sql
SELECT u.Name, COALESCE(x.PaidOrderCount, 0) AS PaidOrderCount
FROM Users u
LEFT JOIN
(
    SELECT UserId, COUNT(*) AS PaidOrderCount
    FROM Orders
    WHERE Status = 'Paid'
    GROUP BY UserId
) x ON x.UserId = u.Id
WHERE u.IsActive = 1;
```

### CTE

```sql
;WITH PaidOrderCounts AS
(
    SELECT UserId, COUNT(*) AS PaidOrderCount
    FROM Orders
    WHERE Status = 'Paid'
    GROUP BY UserId
)
SELECT u.Name, COALESCE(c.PaidOrderCount, 0) AS PaidOrderCount
FROM Users u
LEFT JOIN PaidOrderCounts c ON c.UserId = u.Id
WHERE u.IsActive = 1;
```

### View

```sql
CREATE VIEW dbo.ActiveUserSummary
AS
SELECT u.Id, u.Name, COUNT(o.Id) AS PaidOrderCount
FROM dbo.Users u
LEFT JOIN dbo.Orders o
    ON o.UserId = u.Id
   AND o.Status = 'Paid'
WHERE u.IsActive = 1
GROUP BY u.Id, u.Name;
```

## 3. 執行結果

對同一個「每位 active user 的 paid order 數」需求，derived table、CTE 與 view 可以產生相同結果：

預期結果（需在準備好 Users／Orders seed data 的 SQL Server 執行後核對）：

| Name | PaidOrderCount |
| --- | ---: |
| Ada | 2 |
| Grace | 0 |

```sql
WITH UserTree AS
(
    SELECT Id, ManagerId, Name, 0 AS Level
    FROM Employees
    WHERE ManagerId IS NULL

    UNION ALL

    SELECT e.Id, e.ManagerId, e.Name, t.Level + 1
    FROM Employees e
    JOIN UserTree t ON e.ManagerId = t.Id
)
SELECT *
FROM UserTree
OPTION (MAXRECURSION 100);
```

這個 recursive CTE 從 root（anchor member）開始，再反覆加入下一層（recursive member）。`MAXRECURSION` 預設是 100，範圍為 0–32767；超過上限時 query 以錯誤終止，`0` 則代表不設上限。實務上也應以 FK 或遞迴路徑中的已訪問 Id 防止 cycle。

## 4. SQL Server 背後大概做什麼

### CTE 不是暫存 table

一般 CTE 是 statement scope 的 named query expression：

- 只在緊接著的一個 `SELECT` / `INSERT` / `UPDATE` / `DELETE` / `MERGE` 可用。
- 不會因為寫成 CTE 就 materialize 成 physical table；同一個 CTE 被外層引用兩次時，定義的查詢會各自重新執行。
- optimizer 可能 inline、重新排列或選擇其他 execution plan。
- 如果要重複使用結果、建立索引、保留 statistics，應考慮 `#TempTable` 或其他設計，不要期待 CTE 充當 cache。

CTE 前一個 statement 在 batch 中要以分號結尾，寫成 `;WITH` 可以避免 `Incorrect syntax near the keyword 'WITH'`。

### View

一般 view 保存的是 query definition，不是保存一份獨立資料；每次查詢仍由 optimizer 展開並執行。Indexed view 是特殊功能，有 schema binding、限制與維護成本，屬於進階效能工具，不要把一般 view 與 indexed view 混為一談。

View 不能靠 `ORDER BY` 保存查詢順序（除非搭配 `TOP`／`OFFSET`，而且查詢 view 時仍要自己 `ORDER BY`）。`WITH SCHEMABINDING` 要求兩段式物件名稱；`WITH CHECK OPTION` 會要求透過 view 寫入後資料仍可被該 view 看見。Indexed view 的第一個索引必須是 unique clustered，含 `GROUP BY` 時要有 `COUNT_BIG(*)`；SQL Server Standard edition 直接查 indexed view 需要 `NOEXPAND`。

### Correlated subquery

```sql
SELECT u.Name
FROM Users u
WHERE EXISTS
(
    SELECT 1
    FROM Orders o
    WHERE o.UserId = u.Id
      AND o.Status = 'Paid'
);
```

邏輯上 inner query 參考 outer row；實際 optimizer 可能把它轉成 semi join，不代表一定對每一筆 outer row 開一個獨立 query。

反向查詢優先用 `NOT EXISTS`；`NOT IN` 遇到 NULL 的陷阱見 [[19-SQLServer-NULL與三值邏輯]]。

## 5. 與 C# / EF Core 的關聯

EF Core 可以把多層 LINQ 組成 SQL subquery / JOIN，但不是每個 C# method 都能翻譯。對複雜、穩定且需要 DB 端控制的查詢，可考慮：

- EF Core LINQ projection，維持 type safety。
- database view 對外提供唯讀模型，再映射成 keyless entity。
- `FromSql`／stored procedure，在明確的 DB boundary 使用參數化 SQL。
- SQL 必須交給 DBA 維護、或 LINQ 翻不出來時才使用 raw SQL；CTE 放進 `FromSql` 後不能再接 `Where`／`OrderBy` 等 LINQ，因為 SQL Server 不允許被包進 subquery 的 `WITH`。

若要 composition，把 CTE 改寫成可組合的 `SELECT`／view／TVF；若必須使用 CTE 或 stored procedure，`FromSql` 後立刻 `AsEnumerable()`，在 client 端做後續操作。

EF Core 對 correlated `Any` 會翻成 `EXISTS`：

```csharp
var usersWithPaidOrders = db.Users
    .Where(user => user.Orders.Any(order => order.Status == "Paid"));
Console.WriteLine(usersWithPaidOrders.ToQueryString());
```

實際 SQL（SQL Server provider）：

```sql
WHERE EXISTS (
    SELECT 1
    FROM [Orders] AS [o]
    WHERE [o].[UserId] = [u].[Id] AND [o].[Status] = N'Paid')
```

View 對應成 keyless entity：

```csharp
modelBuilder.Entity<ActiveUserSummary>(entity =>
{
    entity.HasNoKey();
    entity.ToView("ActiveUserSummary");
});
```

這種 entity 不會被追蹤，也不能由 `SaveChanges` insert、update 或 delete。

## 6. 常見誤區

- CTE 只在緊接的一個 statement 有效；需要跨 statement 重用、索引或 statistics 時使用 `#TempTable`。
- View 是 schema 裡可重複使用的查詢定義；查詢效能仍要看展開後的 execution plan。
- Recursive CTE 要防 cycle、深度與資料品質問題。
- 有適當索引時，`EXISTS` 常被 optimizer 轉成 semi join；仍要用 execution plan 檢查列數與存取路徑。
- 為了可讀性使用 CTE 不代表要把一個 query 拆成十層；過度拆分會增加閱讀成本。

## 7. 面試回答

> Derived table 是 FROM 裡的 subquery；CTE 是 statement scope 的命名查詢，適合讓複雜查詢分段與 recursive hierarchy；view 是 schema 裡可重複使用的查詢定義。CTE 不會保存結果；需要重複使用、建索引或保留中間結果時，才考慮 temp table。

## 8. 小練習

1. 將 derived table 改寫成 CTE。
2. 用 recursive CTE 查出員工管理階層。
3. 解釋為什麼 CTE 不一定比 derived table 快。
