---
title: 22 SQL Server Window Functions
tags: [sql-server, mssql, window-functions, ranking]
---

# 22 Window Functions

## 學習目標

- 理解 window function 與 `GROUP BY` 的根本差別。
- 熟悉排名、前後列比較、累計值與 group top-N。
- 能讀懂 `PARTITION BY` 與 `ORDER BY` 的分析範圍。

## 1. 一句話理解

Window function 在「保留每一筆明細 row」的同時，對它所屬的 partition 計算排名、前後值、累計或統計，不會像 `GROUP BY` 一樣把每組壓成一筆。

## 2. 實際 SQL

### 排名

```sql
SELECT
    o.UserId,
    o.Id AS OrderId,
    o.TotalAmount,
    ROW_NUMBER() OVER (
        PARTITION BY o.UserId
        ORDER BY o.CreatedAt DESC, o.Id DESC
    ) AS RowNo,
    RANK() OVER (
        PARTITION BY o.UserId
        ORDER BY o.TotalAmount DESC
    ) AS AmountRank,
    DENSE_RANK() OVER (
        PARTITION BY o.UserId
        ORDER BY o.TotalAmount DESC
    ) AS DenseAmountRank
FROM Orders o;
```

### 前後列與累計

```sql
SELECT
    o.UserId,
    o.CreatedAt,
    o.TotalAmount,
    LAG(o.TotalAmount) OVER (
        PARTITION BY o.UserId
        ORDER BY o.CreatedAt, o.Id
    ) AS PreviousAmount,
    LEAD(o.TotalAmount) OVER (
        PARTITION BY o.UserId
        ORDER BY o.CreatedAt, o.Id
    ) AS NextAmount,
    SUM(o.TotalAmount) OVER (
        PARTITION BY o.UserId
        ORDER BY o.CreatedAt, o.Id
        ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
    ) AS RunningTotal,
    COUNT(*) OVER (PARTITION BY o.UserId) AS UserOrderCount
FROM Orders o;
```

## 3. 執行結果

假設 Ada 的 orders 金額依時間是 `100、100、80`：

| Amount | `ROW_NUMBER` by time | `RANK` by amount | `DENSE_RANK` by amount |
| ---: | ---: | ---: | ---: |
| 100 | 1 | 1 | 1 |
| 100 | 2 | 1 | 1 |
| 80 | 3 | 3 | 2 |

- `ROW_NUMBER()` 每列都給唯一序號，tie 也會分開；要有 deterministic 結果，排序補上唯一 key。
- `RANK()` 相同值同名次，後面名次會跳號：1、1、3。
- `DENSE_RANK()` 相同值同名次，但不跳號：1、1、2。
- `PARTITION BY UserId` 表示每個 user 重新開始排名；沒有 partition 就是全表一個 window。

## 4. SQL Server 背後大概做什麼

### 每個使用者最新一筆

```sql
WITH RankedOrders AS
(
    SELECT
        o.*,
        ROW_NUMBER() OVER (
            PARTITION BY o.UserId
            ORDER BY o.CreatedAt DESC, o.Id DESC
        ) AS rn
    FROM Orders o
)
SELECT *
FROM RankedOrders
WHERE rn = 1;
```

window function 的結果通常不能直接在同一層 `WHERE` 使用，所以用 CTE / derived table 先計算，再在外層篩選。

### 每組排名前三名

```sql
WITH RankedProducts AS
(
    SELECT
        p.CategoryId,
        p.Id,
        p.Score,
        ROW_NUMBER() OVER (
            PARTITION BY p.CategoryId
            ORDER BY p.Score DESC, p.Id
        ) AS rn
    FROM Products p
)
SELECT *
FROM RankedProducts
WHERE rn <= 3;
```

### 前一筆與目前這一筆

```sql
SELECT
    CreatedAt,
    Balance,
    Balance - LAG(Balance) OVER (
        ORDER BY CreatedAt, Id
    ) AS ChangeFromPrevious
FROM BalanceHistory;
```

`LAG` 沒有前一筆時會回 NULL；可以用 `COALESCE` 或保留 NULL 表達「不存在前一筆」。

## 5. 與 C# / EF Core 的關聯

LINQ 的 `GroupBy` 不等於 SQL window function：

- `GroupBy` 常把資料聚成 groups / aggregate，改變 result shape。
- window function 保留每筆 entity / row，再附加分析欄位。

EF Core 對 window function 的 LINQ 翻譯能力依版本與 provider 而異。對複雜 top-N-per-group，先理解目標 SQL，再確認 generated SQL；必要時用明確 projection、view 或 parameterized raw SQL，不要在 memory `ToList()` 後才排序整張大表。

## 6. 常見誤區

- `ROW_NUMBER()` 沒有 deterministic tie-breaker 時，並列 row 的順序可能不穩定。
- `RANK` 與 `DENSE_RANK` 的差別是 tie 後是否跳號。
- `PARTITION BY` 不是 physical partition，也不是 `GROUP BY`；它是 window 的分析分組。
- window function 不是免費排序；`ORDER BY OVER` 可能需要 sort，資料量大時要看 execution plan。
- top-N per group 不要在 C# foreach 對每個 group 查一次，會導向 N+1。

## 7. 面試回答

> Window function 會保留每筆 row，同時在指定 window 內計算排名或累計；`PARTITION BY` 決定每組重新開始，`ORDER BY` 決定分析順序。`ROW_NUMBER` 每列唯一、`RANK` tie 後跳號、`DENSE_RANK` tie 後不跳號。每個 user 最新一筆常用 `ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY CreatedAt DESC)`，再在外層取 `rn = 1`。

## 8. 小練習

1. 查每個 user 最新一筆 order。
2. 查每個 category score 前三名。
3. 用 `LAG` 計算每個 account balance 相對前一筆的變化。
