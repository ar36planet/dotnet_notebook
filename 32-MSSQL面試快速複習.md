---
title: 32 MSSQL 面試快速複習
tags: [sql-server, mssql, interview, ef-core]
---

# 32 MSSQL / EF Core 面試快速複習

## 學習目標

- 把 SQL Server 常見面試題壓縮成 30 秒回答。
- 面試時能同時講出 SQL 語意、execution / concurrency trade-off 與 .NET 關聯。
- 避免只背關鍵字而無法解釋實際風險。

## 1. 一句話理解

SQL Server 面試不只是問語法，而是看你能不能從 result set、NULL、query plan、index、transaction、concurrency 一路推到 Web API 與 EF Core 的實際行為。

## 2. 實際 SQL

### SQL 題型 snippets

```sql
-- JOIN：保留沒有 order 的 user
SELECT u.Id, u.Name, COUNT(o.Id) AS PaidOrderCount
FROM Users u
LEFT JOIN Orders o
    ON o.UserId = u.Id
   AND o.Status = 'Paid'
GROUP BY u.Id, u.Name
HAVING COUNT(o.Id) > 0;
```

```sql
-- CTE + window：每個 user 最新 order
WITH Ranked AS
(
    SELECT o.*,
        ROW_NUMBER() OVER
        (
            PARTITION BY UserId
            ORDER BY CreatedAt DESC, Id DESC
        ) AS rn
    FROM Orders o
)
SELECT * FROM Ranked WHERE rn = 1;
```

```sql
-- NULL：不存在不是等於 NULL
SELECT * FROM Users WHERE DeletedAt IS NULL;
```

## 3. 執行結果

### SQL 基礎題

| 問題 | 30 秒回答 |
| --- | --- |
| `JOIN` 類型？ | INNER 只保留配對；LEFT 保留左表全部、右側未配對補 NULL；RIGHT 是反向保留；FULL 兩側未配對都保留；CROSS 是笛卡兒積；SELF 是同表不同 alias 相互配對。 |
| `ON` vs `WHERE`？ | ON 決定 JOIN 配對，WHERE 過濾完成後結果；LEFT JOIN 的右側條件放 WHERE 可能排掉右側 NULL，使結果接近 INNER JOIN。 |
| `GROUP BY` vs `HAVING`？ | WHERE 在 grouping 前過濾 rows；GROUP BY 形成 groups；HAVING 在 grouping 後過濾 aggregate 結果。 |
| Subquery vs CTE？ | CTE 是單一 statement scope 的命名 query expression，主要改善可讀性與 recursive query；不等於暫存 table。 |
| `UNION` vs `UNION ALL`？ | UNION 合併後去重，通常需要額外 sort / hash；UNION ALL 不去重，通常較快，前提是允許重複。 |
| Window function？ | 保留每筆 row，同時在 partition 內計算排名、前後值或累計；不等同 GROUP BY。 |
| `ROW_NUMBER` / `RANK` / `DENSE_RANK`？ | ROW_NUMBER 每列唯一；RANK tie 後跳號；DENSE_RANK tie 後不跳號。 |
| NULL？ | 三值邏輯包含 TRUE / FALSE / UNKNOWN；`= NULL` 不會匹配，使用 `IS NULL`。 |

## 4. SQL Server 背後大概做什麼

### Index 題

| 問題 | 30 秒回答 |
| --- | --- |
| Clustered vs nonclustered？ | Clustered leaf 是 table data，一表最多一個；nonclustered 是額外 B-tree，可有多個，leaf 含 key / locator / included columns。 |
| Composite index？ | `(A, B)` 先按 A 再按 B；leading key / leftmost 重要，單獨查 B 通常不能像查 A 一樣直接 seek。 |
| Covering index？ | Index 已包含 filter / join / projection 需要的資訊，可能避免回 base table 的 Key Lookup；但增加 storage 與 write cost。 |
| Seek vs scan？ | Seek 在 index 中定位範圍；scan 讀大量或全部 index/table。Scan 不一定錯，取決於 selectivity 與 query 要讀多少資料。 |
| Key Lookup？ | nonclustered index 找到 key 後，再回 clustered index / heap 取其他欄位；大量 lookup 可能成為瓶頸。 |

### Transaction / concurrency 題

| 問題 | 30 秒回答 |
| --- | --- |
| ACID？ | Atomicity 全成或全敗；Consistency 維持 invariant；Isolation 控制併發可見性；Durability commit 後持久。 |
| Isolation level？ | 越高通常一致性越強、locking / blocking 成本越高；READ COMMITTED 是常見預設，SNAPSHOT 用 row version，SERIALIZABLE 提供最強隔離但可能降低 concurrency。 |
| Dirty / non-repeatable / phantom？ | Dirty 讀到未 commit；non-repeatable 同一 transaction 兩次讀同一 row 得不同值；phantom 第二次讀出現新增／消失的符合範圍 row。 |
| Blocking vs deadlock？ | Blocking 是等待別人的 lock；deadlock 是互相等待 cycle，SQL Server 選 victim rollback。 |
| Optimistic concurrency？ | 不先長時間鎖住 row，以 `rowversion` / token 在 update 時驗證原始版本，衝突時回錯誤並由 application 處理。 |

### Performance 題

| 問題 | 30 秒回答 |
| --- | --- |
| Execution plan？ | 顯示 optimizer 選的 scan / seek / join / sort 等 operators；要比較 actual vs estimated rows、logical reads、CPU、duration 與 warnings。 |
| SARGability？ | 讓 predicate 能利用 column key 定位；對 indexed column 套 `CONVERT` / `YEAR` / `LOWER` 可能妨礙 seek，應改 range 或 computed index 等方案。 |
| Parameter sniffing？ | 編譯時用當下 parameter 建 plan；資料分布不均時同一 cached plan 對其他參數可能不理想。先用 Query Store / actual plan 驗證。 |
| `SELECT *` 問題？ | 多讀欄位、增加 network / memory、讓 covering index 更難、也讓 API contract 暴露更多資料。 |
| OFFSET 為何越翻越慢？ | SQL Server 仍可能需要找到並丟棄前面大量 rows；大資料 feed 可用 keyset pagination。 |

### EF Core 題

| 問題 | 30 秒回答 |
| --- | --- |
| `IEnumerable` vs `IQueryable`？ | IEnumerable 在 memory 執行；IQueryable 保留 expression tree，EF Core 可翻成 SQL，到 `ToListAsync` 等 terminal operation 才執行。 |
| Tracking vs `AsNoTracking`？ | Tracking 保存 entity state、identity 與修改偵測；read-only query / DTO 通常用 no-tracking 或 projection。 |
| `Include` vs projection？ | Include 載入 entity graph；projection 只取 DTO 所需欄位，對 API read model 常較精準。兩者都要觀察 SQL shape。 |
| N+1？ | 1 次查 parent + N 次查 child；改成 projection、批次 query、合理 Include / split query。 |
| `FirstOrDefault` vs `SingleOrDefault`？ | First 允許多筆只取第一；Single 要求最多一筆，>1 就錯，適合 unique invariant。 |
| `FindAsync`？ | 以 primary key lookup，先查同一 `DbContext` 的 tracked entity，沒有才查 DB；不是任意 predicate 查詢。 |
| `SaveChangesAsync`？ | 偵測 tracked changes、產生 write commands，通常在需要時包 transaction；實際 commit boundary 依 provider / transaction scope。 |

## 5. 與 C# / EF Core 的關聯

一個完整回答應能把 layers 串起來：

```text
Controller receives CancellationToken
    ↓
Service composes IQueryable
    ↓
EF Core translates LINQ expression tree
    ↓
SQL Server optimizer chooses plan
    ↓
Index / lock / isolation affect execution
    ↓
ToListAsync materializes DTO
    ↓
ASP.NET Core serializes JSON
```

面試被問「這段 code 有什麼問題」時，先問：

```csharp
var users = await db.Users.ToListAsync(ct);
foreach (var user in users)
{
    user.Orders = await db.Orders
        .Where(x => x.UserId == user.Id)
        .ToListAsync(ct);
}
```

回答應包含：可能 N+1、過早 materialization、tracking graph 成本、是否需要 `AsNoTracking`、能否 projection、users / orders index 與資料量。

## 6. 常見誤區

- 只背「seek 好、scan 壞」；要看實際 rows、selectivity、projection 與 plan。
- 只說 `WITH (NOLOCK)` 可以避免 blocking；忽略 dirty / missing / duplicated reads。
- 把 CTE 當 temp table，把 `Include` 當成 N+1 的萬用解。
- 把 `SingleOrDefault` 當成效能選擇，忽略它其實在驗證唯一性。
- 只談 EF Core，不知道最後仍是 SQL Server index / lock / plan 在執行。

## 7. 面試回答

> 我會先確認查詢語意與資料量，再用 actual execution plan、logical reads 與 estimated/actual rows 找問題。Index 要從 where、join、order、projection 設計，注意 composite leading key 與 write cost。併發寫入用 transaction / isolation / rowversion 處理，API read query 通常 projection + AsNoTracking，並確認沒有 N+1。EF Core 不是黑盒，generated SQL 最後仍交給 SQL Server optimizer、index 與 lock manager。

## 8. 小練習

1. 用 30 秒說明「為什麼 LEFT JOIN 的條件不能隨便放 WHERE」。
2. 用 30 秒比較 `FirstOrDefaultAsync`、`SingleOrDefaultAsync`、`FindAsync`。
3. 看一段含 `ToListAsync` 的 EF code，指出 SQL execution boundary。
