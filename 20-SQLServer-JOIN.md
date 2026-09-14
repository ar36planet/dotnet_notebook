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
INSERT #Orders VALUES
    (101, 1, 'Paid'),
    (102, 1, 'Cancelled'),
    (103, 2, 'Pending'),
    (104, 9, 'Paid'); -- 孤兒 order，沒有對應 user
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

-- RIGHT：保留右表全部資料
SELECT u.Id, u.Name, o.Id AS OrderId
FROM #Users u
RIGHT JOIN #Orders o ON o.UserId = u.Id;

-- FULL：兩邊沒有配對的 row 也保留
SELECT u.Id, u.Name, o.Id AS OrderId
FROM #Users u
FULL OUTER JOIN #Orders o ON o.UserId = u.Id;

-- CROSS：笛卡兒積；3 users × 4 orders = 12 rows
SELECT u.Name, o.Id AS OrderId
FROM #Users u
CROSS JOIN #Orders o;

-- SELF JOIN 需要另外準備 Employees(Id, Name, ManagerId) 表，本節不把它混進上述可直接執行的 script。
```

## 3. 執行結果

預期結果（需在 SQL Server 執行後核對）：

| JOIN | rows | 未配對列 |
| --- | ---: | --- |
| `INNER JOIN` | 3 | 無 |
| `LEFT JOIN` | 4 | Linus |
| `RIGHT JOIN` | 4 | Order 104 |
| `FULL OUTER JOIN` | 5 | Linus、Order 104 |
| `CROSS JOIN` | 12 | 不適用 |

`LEFT JOIN` 的列內容：

| User | OrderId |
| --- | --- |
| Ada | 101 |
| Ada | 102 |
| Grace | 103 |
| Linus | `NULL` |

SQL 一對多 JOIN 會複製左側 row；Ada 有兩張 order，所以 Ada 出現兩次，user 表本身沒有重複。

### `ON` vs `WHERE`

```sql
-- A：所有 user 都保留；沒有 Paid order 的 user 右側補 NULL
SELECT u.Name, o.Id AS PaidOrderId
FROM #Users u
LEFT JOIN #Orders o
    ON u.Id = o.UserId
   AND o.Status = 'Paid';

-- B：右表一般比較放在 WHERE；結果等同 INNER JOIN + WHERE
SELECT u.Name, o.Id AS PaidOrderId
FROM #Users u
LEFT JOIN #Orders o
    ON u.Id = o.UserId
WHERE o.Status = 'Paid';
```

預期結果：

```text
A：Ada 101、Grace NULL、Linus NULL
B：Ada 101
```

每個 user 取最新一張 order 時，可用 `OUTER APPLY` 保留沒有 order 的 user：

```sql
SELECT u.Name, latest.Id AS LatestOrderId
FROM #Users u
OUTER APPLY
(
    SELECT TOP (1) o.Id
    FROM #Orders o
    WHERE o.UserId = u.Id
    ORDER BY o.Id DESC
) latest;
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

實際執行時，Query Optimizer 可能選 Nested Loops、Hash Match、Merge Join 或 SQL Server 2017+ 的 Adaptive Join；會依列數、索引、排序與統計資訊選擇。join hint 可以強制演算法，但一般不應先用 hint 取代計畫分析，詳見 [[24-Execution-Plan與Query-Performance]]。

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

這個查詢在 EF Core 10 的 single-query mode 會產生一個 LEFT JOIN 子查詢；只有加上 `AsSplitQuery()` 或設定 split-query behavior 才會拆成多句 SQL。若使用 `Include`，它是 entity graph loading，和 DTO 投影的資料形狀／追蹤成本不同。

實際 SQL（SQLite provider；SQL Server provider 只會改識別字引號）：

```sql
SELECT "u"."Id", "u"."Name", "o0"."Id"
FROM "Users" AS "u"
LEFT JOIN (
    SELECT "o"."Id", "o"."UserId"
    FROM "Orders" AS "o"
    WHERE "o"."Status" = 1
) AS "o0" ON "u"."Id" = "o0"."UserId"
ORDER BY "u"."Id"
```

EF Core 10／.NET 10 也提供 `Queryable.LeftJoin`／`RightJoin`；舊版通常要用 `GroupJoin` + `SelectMany` + `DefaultIfEmpty` 的特定形狀：

```csharp
var result = db.Users.LeftJoin(
    db.Orders.Where(order => order.Status == OrderStatus.Paid),
    user => user.Id,
    order => order.UserId,
    (user, order) => new { user.Name, OrderId = (int?)order!.Id });
```

## 6. 常見誤區

- `LEFT JOIN` 不是「一定保留右表所有資料」；它保留的是左表所有資料。
- 一對多 JOIN 後 `COUNT(*)` 算的是配對後的列數，不是 user 數；要算 user 數用 `COUNT(DISTINCT u.Id)`。LEFT JOIN 後要算每個 user 的 order 數，用 `COUNT(o.Id)`，不要用 `COUNT(*)`，因為補 NULL 的列也會被 `COUNT(*)` 算進去。
- 右表欄位的一般比較條件放到 `WHERE`，`LEFT JOIN` 就退化成 `INNER JOIN`；`IS NULL` 等對 NULL 成立的條件是例外。
- `RIGHT JOIN` 可以改寫成交換表順序的 `LEFT JOIN`；團隊常偏好後者。
- `CROSS JOIN` 沒有 join predicate，資料量可能乘法爆炸。

## 7. 面試回答

> `INNER JOIN` 只回傳有配對的 rows；`LEFT JOIN` 保留左側全部 rows，右側沒有匹配時用 NULL 補齊。條件放在 `ON` 是限制配對，條件放在 `WHERE` 是過濾完成後結果；對 `LEFT JOIN` 來說，把右側條件放到 WHERE 常會排掉右側 NULL，讓結果接近 INNER JOIN。實際 JOIN algorithm 由 optimizer 依統計與索引選擇，不是由關鍵字固定。

## 8. 小練習

1. 查出所有 user，以及每個 user 的 Paid order 數，包含 0 筆的 user。
2. 將右側 `Status = 'Paid'` 分別放在 ON / WHERE，比較結果。
3. 解釋為什麼 `CROSS JOIN` 可能造成資料量快速成長。
