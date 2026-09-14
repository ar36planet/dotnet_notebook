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

Window function 在保留每一筆明細列的同時，對它所屬的 partition 計算排名、前後值、累計或統計，不會像 `GROUP BY` 一樣把每組壓成一筆。

## 2. 實際 SQL

### 排名

固定輸入（`CreatedAt` 依時間遞增，`Id` 是唯一 tie-breaker）：

```sql
DECLARE @Orders TABLE
(
    Id int,
    UserId int,
    CreatedAt datetime2(0),
    TotalAmount decimal(10, 2)
);

INSERT @Orders VALUES
    (1, 1, '2026-09-01 09:00:00', 100.00),
    (2, 1, '2026-09-02 09:00:00', 100.00),
    (3, 1, '2026-09-03 09:00:00', 80.00);
```

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
    ,NTILE(2) OVER (
        PARTITION BY o.UserId
        ORDER BY o.TotalAmount DESC, o.Id DESC
    ) AS AmountBucket
FROM @Orders o;
```

累計明確寫 `ROWS` 是為了按實體列累加。若只寫 `ORDER BY CreatedAt` 而省略 frame，預設是 `RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW`；同一個 `CreatedAt` 的 peer rows 會拿到相同累計值。`RANGE` 不能搭配數字偏移的 `PRECEDING`／`FOLLOWING`，需要固定列數時使用 `ROWS`。

排名函數的 `ORDER BY` 必填，不能附加 `ROWS`／`RANGE`；三個排名函數與 `NTILE` 都回傳 `bigint`。

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
FROM @Orders o;
```

## 3. 執行結果

以下結果以固定輸入與相同 SQL 核對；本機沒有 SQL Server，SQL Server 請重跑確認：

| Id | Amount | `ROW_NUMBER` by time | `RANK` by amount | `DENSE_RANK` by amount |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 100 | 3 | 1 | 1 |
| 2 | 100 | 2 | 1 | 1 |
| 3 | 80 | 1 | 3 | 2 |

- `ROW_NUMBER()` 每列都給唯一序號，同值也會分開；排序要補上唯一 key。
- `RANK()` 相同值同名次，後面名次會跳號：1、1、3。
- `DENSE_RANK()` 相同值同名次，但不跳號：1、1、2。
- `PARTITION BY UserId` 表示每個 user 重新開始排名；沒有 partition 就是全表一個 window。

## 4. 常用查詢樣板與執行計畫

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

視窗函數只能出現在 `SELECT` 與 `ORDER BY`，寫在同一層 `WHERE` 會得到：

```text
Msg 4108: Windowed functions can only appear in the SELECT or ORDER BY clauses.
```

所以要用 CTE／derived table 先計算，再在外層篩選。

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

`LAG(expression, offset, default)` 的 `offset` 預設是 1、`default` 預設是 NULL；例如 `LAG(Balance, 1, 0)` 可以直接把第一筆的差值基準設成 0。SQL Server 2022+ 還支援 `IGNORE NULLS`／`RESPECT NULLS`，預設是 `RESPECT NULLS`。

## 5. 與 C# / EF Core 的關聯

LINQ 的 `GroupBy` 改變結果形狀；SQL window function 保留每筆列並附加分析欄位。EF Core 6+ 對特定的 `GroupBy` + `OrderBy` + `First`／`Take` 形狀能翻成 `ROW_NUMBER()`，但沒有直接對應 `LAG`、`LEAD`、`RANK` 或 `SUM() OVER` 的 LINQ API。

```csharp
var latestOrders = db.Orders
    .GroupBy(order => order.UserId)
    .Select(group => group
        .OrderByDescending(order => order.CreatedAt)
        .ThenByDescending(order => order.Id)
        .First());
Console.WriteLine(latestOrders.ToQueryString());
```

實際產生的 SQL 會包含 `ROW_NUMBER() OVER(PARTITION BY ... ORDER BY ...)` 與外層 `WHERE [row] <= 1`。若改寫成不受支援的 `SelectMany(group => group.OrderByDescending(...).Take(3))`，EF Core 會在執行時丟 `InvalidOperationException`。需要 `LAG`／`LEAD`／`RANK`／`SUM() OVER` 時，使用參數化 raw SQL、view 或 keyless entity；不要先 `ToList()` 把大型資料集拉到記憶體再排序。

## 6. 常見誤區

- `ROW_NUMBER()` 沒有唯一的排序鍵時，同值列的順序不保證固定。
- `RANK` 與 `DENSE_RANK` 的差別是 tie 後是否跳號。
- `PARTITION BY` 只是視窗的分組範圍，與資料表分割和 `GROUP BY` 不同。
- `ORDER BY` 的索引 key 順序應先放 `PARTITION BY` 欄位，再放 `ORDER BY` 欄位；是否需要 Sort 要看 execution plan。
- 每組前 N 筆不要在 C# 迴圈裡對每組各查一次，那是 N+1。
- `ROW_NUMBER`、`RANK`、`DENSE_RANK`、`NTILE` 回傳 `bigint`；需要映射到 C# 時使用 `long`。

## 7. 面試回答

> Window function 會保留每筆 row，同時在指定 window 內計算排名或累計；`PARTITION BY` 決定每組重新開始，`ORDER BY` 決定分析順序。`ROW_NUMBER` 每列唯一、`RANK` tie 後跳號、`DENSE_RANK` tie 後不跳號。每個 user 最新一筆常用 `ROW_NUMBER() OVER (PARTITION BY UserId ORDER BY CreatedAt DESC)`，再在外層取 `rn = 1`。

## 8. 小練習

1. 查每個 user 最新一筆 order。
2. 查每個 category score 前三名。
3. 用 `LAG` 計算每個 account balance 相對前一筆的變化。
