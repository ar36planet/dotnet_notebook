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

### Derived table

```sql
SELECT x.UserId, x.OrderCount
FROM
(
    SELECT UserId, COUNT(*) AS OrderCount
    FROM Orders
    GROUP BY UserId
) x
WHERE x.OrderCount >= 3;
```

### CTE

```sql
WITH OrderCounts AS
(
    SELECT UserId, COUNT(*) AS OrderCount
    FROM Orders
    GROUP BY UserId
)
SELECT u.Name, c.OrderCount
FROM Users u
JOIN OrderCounts c ON c.UserId = u.Id
WHERE c.OrderCount >= 3;
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

對同一個「每位 active user 的 paid order 數」需求，derived table 與 CTE 可以產生相同 result set：

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

這個 recursive CTE 從 root（anchor member）開始，再反覆加入下一層（recursive member）。`MAXRECURSION` 是防止資料錯誤或 cycle 造成無限遞迴的保護；實務上也應以 schema constraint / visited logic 確保 hierarchy 合理。

## 4. SQL Server 背後大概做什麼

### CTE 不是暫存 table

一般 CTE 是 statement scope 的 named query expression：

- 只在緊接著的一個 `SELECT` / `INSERT` / `UPDATE` / `DELETE` / `MERGE` 可用。
- 通常不會因為寫成 CTE 就 materialize 成 physical table。
- optimizer 可能 inline、重新排列或選擇其他 execution plan。
- 如果要重複使用結果、建立索引、保留 statistics，應考慮 `#TempTable` 或其他設計，不要期待 CTE 充當 cache。

### View

一般 view 保存的是 query definition，不是保存一份獨立資料；每次查詢仍由 optimizer 展開並執行。Indexed view 是特殊功能，有 schema binding、限制與維護成本，屬於進階效能工具，不要把一般 view 與 indexed view 混為一談。

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

## 5. 與 C# / EF Core 的關聯

EF Core 可以把多層 LINQ 組成 SQL subquery / JOIN，但不是每個 C# method 都能翻譯。對複雜、穩定且需要 DB 端控制的查詢，可考慮：

- EF Core LINQ projection，維持 type safety。
- database view 對外提供 read model，再映射成 keyless entity。
- `FromSql` / stored procedure，在明確的 DB boundary 使用參數化 SQL。
- 將 query 寫成 CTE，若 provider / raw SQL 需要 readable SQL。

不要因為 CTE 看起來像一張「中間表」就以為 EF / SQL Server 會自動把它存起來。

## 6. 常見誤區

- CTE 不等於 `#TempTable`，沒有自動 persistence、索引或可跨 statement reuse。
- View 不等於 cache；查詢效能仍要看展開後的 execution plan。
- Recursive CTE 要防 cycle、深度與資料品質問題。
- correlated subquery 不一定慢，也不一定快；看 optimizer、索引、row count 與 plan。
- 為了可讀性使用 CTE 不代表要把一個 query 拆成十層；過度拆分會增加閱讀成本。

## 7. 面試回答

> Derived table 是 FROM 裡的 subquery；CTE 是 statement scope 的命名 query expression，適合讓複雜查詢分段與 recursive hierarchy；view 是 database schema 層的 reusable query contract。一般 CTE / view 都不等於 materialized temporary table，實際效能仍看 optimizer 與 execution plan。需要重複使用、建索引或保留中間結果時，才考慮 temp table。

## 8. 小練習

1. 將 derived table 改寫成 CTE。
2. 用 recursive CTE 查出員工管理階層。
3. 解釋為什麼 CTE 不一定比 derived table 快。
