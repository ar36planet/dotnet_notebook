---
title: 24 Execution Plan 與 Query Performance
tags: [sql-server, mssql, execution-plan, sargability, performance]
---

# 24 Execution Plan 與 Query Performance

## 學習目標

- 分辨 estimated / actual execution plan。
- 看懂常見 operators 與「估計 rows vs 實際 rows」。
- 針對 SARGability、implicit conversion、parameter sniffing、pagination 與 N+1 做第一輪診斷。

## 1. 一句話理解

SQL 能得到正確結果只是第一關；execution plan 告訴你 SQL Server 如何掃描、join、排序與讀資料，效能問題通常要從 plan、資料分布與實際 workload 一起判斷。

## 2. 實際 SQL

```sql
SET STATISTICS IO, TIME ON;

SELECT u.Id, u.Name
FROM Users u
WHERE u.Email = @email;

SET STATISTICS IO, TIME OFF;
```

在 SSMS：

- `Display Estimated Execution Plan`：編譯後預估的 plan，不實際執行 query。
- `Include Actual Execution Plan`：實際執行後，包含 runtime row count、warnings 等資訊。

也可用：

```sql
SET SHOWPLAN_XML ON;
-- 之後的 statement 只回傳 estimated plan，不執行。
SET SHOWPLAN_XML OFF;
```

## 3. 執行結果

執行 plan 時優先比較：

| 觀察點 | 它可能告訴你什麼 |
| --- | --- |
| Estimated Rows vs Actual Rows 差很多 | statistics、parameter sensitivity、predicate selectivity 或 data skew 問題 |
| Table / Index Scan | 沒有合適 index、查詢需要大量資料，或 optimizer 判斷 scan 比 seek 便宜 |
| Index Seek 後大量 Key Lookup | index 沒 covering，可能需要補 INCLUDE、改 projection 或接受 lookup |
| Sort | `ORDER BY` / window / DISTINCT 需要排序；可能耗 memory 或 spill 到 tempdb |
| Hash Match | 常見於 hash join / aggregate；輸入量大時要注意 memory grant |
| Nested Loops | 小 outer input 搭配有索引的 inner lookup 常有效；outer row 很大時可能放大成本 |
| Merge Join | 兩邊依 join key 排序時有效；排序成本與輸入順序重要 |

Plan 上的 cost percentage 是相對於該 plan 的估算，不是 wall-clock 百分比；不能只挑最高百分比的 operator 就斷言它是 root cause。

## 4. SQL Server 背後大概做什麼

### SARGability

可 SARGable 的範例：

```sql
WHERE CreatedAt >= @from
  AND CreatedAt <  @to
```

容易妨礙 index seek 的範例：

```sql
WHERE CONVERT(date, CreatedAt) = @day;
WHERE YEAR(CreatedAt) = 2026;
WHERE LOWER(Email) = LOWER(@email);
```

這不是絕對「一定 scan」的保證，但把 function / conversion 套在 indexed column 上，常讓 optimizer 難以直接用原始 key 定位。可改成 range predicate、建立適當 computed column / index，或在 schema 端統一資料。

### Implicit conversion

```sql
-- 如果 UserId 是 int，卻傳入 nvarchar parameter，可能發生 implicit conversion。
WHERE UserId = @stringParameter;
```

型別 precedence、column / parameter type 不一致時，SQL Server 可能在執行時轉換大量資料，並在 plan 顯示警告。ADO.NET / EF Core parameter type 要和欄位對齊，不要把所有輸入都當 string。

### 常見查詢問題

- `SELECT *`：多讀欄位、放大 network / memory、讓 covering index 更難設計。
- 不必要 JOIN：增加 row 數、join cost 與 duplicate risk。
- `%xxx`：前置 wildcard 通常無法從一般 B-tree 直接定位；`xxx%` 通常較有機會 seek。
- 不必要 `DISTINCT`：可能觸發 Sort / Hash Aggregate，且常是 JOIN / data model bug 的掩蓋。
- 大量 `OFFSET`：資料庫仍需跳過前面 rows；頁數越後面通常越昂貴。
- N+1：不是一條 query 的 plan，而是 application 產生大量 round trips。

### Parameter sniffing

SQL Server 可能在 stored procedure / parameterized query 編譯時使用當下參數值建立 plan；資料分布高度不均時，這個 plan 對另一個參數可能不理想。SQL Server 2022+ 還有 Parameter Sensitive Plan optimization，但仍應先用實際 plan、Query Store、statistics 與 workload 驗證，不要把 `OPTION (RECOMPILE)` 當萬用藥。

## 5. 與 C# / EF Core 的關聯

```csharp
var query = db.Users
    .Where(x => x.IsActive)
    .OrderBy(x => x.CreatedAt)
    .Select(x => new UserListItem(x.Id, x.Name));

Console.WriteLine(query.ToQueryString());
var result = await query.ToListAsync(cancellationToken);
```

診斷步驟：

1. 先看 generated SQL，確認沒有過早 `ToList()`。
2. 用相同 parameters 在 SSMS / Query Store 觀察 actual plan。
3. 比較 estimated / actual rows、logical reads、CPU、duration。
4. 檢查 index、statistics、data distribution 與 projection。
5. 修改後重新量測，不以單次本機結果代替 production workload。

## 6. 常見誤區

- Cost percentage 不是實際時間比例，也不能跨不同 query 比較。
- `NOLOCK` 不是正確效能修復，可能讀到 dirty / duplicated / missing rows。
- 一看到 scan 就加 index；先確認 query selectivity 與掃描是否其實合理。
- Estimated plan 沒有 runtime row count；處理 parameter sensitivity / data skew 時常需要 actual plan。
- `ToQueryString()` 只協助看 SQL，不會顯示實際執行時間與完整 plan。

## 7. 面試回答

> 我會用 actual execution plan、logical reads、CPU、duration 和 estimated/actual rows 差異診斷，而不是只看 SQL 長短。常見問題包含不具 SARGability 的 function、implicit conversion、沒有合適 index、Key Lookup、Sort spill、N+1、過深 OFFSET 與 parameter sensitivity。Cost percentage 是 plan 內相對估算，不能直接當成實際時間百分比。

## 8. 小練習

1. 將 `WHERE CONVERT(date, CreatedAt) = @day` 改成 range predicate。
2. 為什麼 `SELECT *` 會讓 query performance 與 index design 更難？
3. 找一條 EF Core query，輸出 generated SQL 並比較 actual rows。
