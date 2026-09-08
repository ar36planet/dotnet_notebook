---
title: 20 SQL Server JOIN
tags: [sql-server, mssql, join, ef-core]
---

# 20 SQL Server JOIN

## 學習目標

- 理解每種 JOIN 的 row set 語意，而不是只背語法。
- 清楚判斷 `ON` 與 `WHERE` 條件對 `LEFT JOIN` 的影響。
- 能把 JOIN 結果和 C# / EF Core projection 對起來。

## 1. 一句話理解

JOIN 是把兩個 row set 依條件配對；`INNER JOIN` 只保留有配對的資料，outer join 還會保留沒有配對的一側，並以 NULL 補上另一側欄位。

## 2. 實際 SQL

準備簡化資料：

```sql
CREATE TABLE #Users (Id int PRIMARY KEY, Name varchar(20) NOT NULL);
CREATE TABLE #Orders (Id int PRIMARY KEY, UserId int NOT NULL, Status varchar(20) NOT NULL);

INSERT #Users VALUES (1, 'Ada'), (2, 'Grace'), (3, 'Linus');
INSERT #Orders VALUES (101, 1, 'Paid'), (102, 1, 'Cancelled'), (103, 2, 'Pending');
```

各種 JOIN：

```sql
-- INNER：只保留有 order 的 user
SELECT u.Id, u.Name, o.Id AS OrderId
FROM #Users u
INNER JOIN #Orders o ON o.UserId = u.Id;

-- LEFT：保留所有 user；沒有 order 的 Linus 仍出現
SELECT u.Id, u.Name, o.Id AS OrderId
FROM #Users u
LEFT JOIN #Orders o ON o.UserId = u.Id;

-- RIGHT：語意等同從右側保留，實務上常改寫成 LEFT 以提升可讀性
SELECT u.Id, u.Name, o.Id AS OrderId
FROM #Users u
RIGHT JOIN #Orders o ON o.UserId = u.Id;

-- FULL：兩邊沒有配對的 row 也保留
SELECT u.Id, u.Name, o.Id AS OrderId
FROM #Users u
FULL OUTER JOIN #Orders o ON o.UserId = u.Id;

-- CROSS：笛卡兒積；3 users × 3 orders = 9 rows
SELECT u.Name, o.Id AS OrderId
FROM #Users u
CROSS JOIN #Orders o;

-- SELF：同一張 table 自己 join 自己，例如員工與主管
SELECT e.Name AS Employee, m.Name AS Manager
FROM Employees e
LEFT JOIN Employees m ON m.Id = e.ManagerId;
```

## 3. 執行結果

`LEFT JOIN` 的概念結果：

| User | OrderId |
| --- | --- |
| Ada | 101 |
| Ada | 102 |
| Grace | 103 |
| Linus | `NULL` |

SQL 一對多 JOIN 會複製左側 row；Ada 有兩張 order，所以 Ada 出現兩次。不是資料庫重複了 user，而是 result set 的 relational shape 改變了。

### `ON` vs `WHERE`

```sql
-- A：保留沒有 Paid order 的 user，右側非 Paid 時補 NULL
SELECT u.Name, o.Id AS PaidOrderId
FROM Users u
LEFT JOIN Orders o
    ON u.Id = o.UserId
   AND o.Status = 'Paid';

-- B：先 LEFT JOIN，再用 WHERE 排掉右側 NULL；效果接近 INNER JOIN
SELECT u.Name, o.Id AS PaidOrderId
FROM Users u
LEFT JOIN Orders o
    ON u.Id = o.UserId
WHERE o.Status = 'Paid';
```

## 4. SQL Server 背後大概做什麼

JOIN 的邏輯處理可以先想成：

```text
左側 rows × 右側 rows
    ↓ 用 ON predicate 判斷配對
產生 matched rows
    ↓ outer join 再補上未配對一側的 NULL extended row
    ↓ WHERE 再過濾結果
```

實際執行時，Query Optimizer 可能選 Nested Loops、Hash Match 或 Merge Join；這不是 JOIN 關鍵字直接指定的固定演算法，會依 row 數、index、排序與統計資訊選擇，詳見 [[24-Execution-Plan與Query-Performance]]。

`LEFT JOIN ... ON right.Status = 'Paid'` 是把條件放在「配對規則」；`WHERE right.Status = 'Paid'` 是把條件放在「配對完成後的結果過濾」。右側為 NULL 時，`WHERE` 條件結果是 UNKNOWN，因此 row 被排除。

## 5. 與 C# / EF Core 的關聯

EF Core navigation + projection：

```csharp
var users = await db.Users
    .Select(u => new UserSummaryDto(
        u.Id,
        u.Name,
        u.Orders
            .Where(o => o.Status == OrderStatus.Paid)
            .Select(o => o.Id)
            .ToList()))
    .ToListAsync(cancellationToken);
```

這是在 LINQ 中表達關聯 projection；provider 可能產生 JOIN、subquery 或多段 SQL，不能只用 C# 表面語法推斷。若使用 `Include`，它是 entity graph loading，和 DTO projection 的資料 shape / tracking 成本不同。

## 6. 常見誤區

- `LEFT JOIN` 不是「一定保留右表所有資料」；它保留的是左表所有資料。
- 一對多 JOIN 會讓左側 row 重複；直接 `COUNT(*)` 可能把 row 數算錯，需理解 `COUNT(DISTINCT ...)` 或 grouping。
- 把右側條件從 `ON` 移到 `WHERE`，可能把 outer join 變成 inner-like semantics。
- `RIGHT JOIN` 可以改寫成交換表順序的 `LEFT JOIN`，團隊通常偏好後者可讀性。
- `CROSS JOIN` 沒有 join predicate，資料量可能乘法爆炸。

## 7. 面試回答

> `INNER JOIN` 只回傳有配對的 rows；`LEFT JOIN` 保留左側全部 rows，右側沒有匹配時用 NULL 補齊。條件放在 `ON` 是限制配對，條件放在 `WHERE` 是過濾完成後結果；對 `LEFT JOIN` 來說，把右側條件放到 WHERE 常會排掉右側 NULL，讓結果接近 INNER JOIN。實際 JOIN algorithm 由 optimizer 依統計與索引選擇，不是由關鍵字固定。

## 8. 小練習

1. 查出所有 user，以及每個 user 的 Paid order 數，包含 0 筆的 user。
2. 將右側 `Status = 'Paid'` 分別放在 ON / WHERE，比較結果。
3. 解釋為什麼 `CROSS JOIN` 可能造成資料量快速成長。
