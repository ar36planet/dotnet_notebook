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
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

SELECT o.OrderId, o.CustomerId, o.TotalAmount
FROM Orders o
WHERE o.InvoiceNo = @invoiceNo;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
```

在 SSMS：

- `Display Estimated Execution Plan`：編譯後預估的 plan，不實際執行 query。
- `Include Actual Execution Plan`：實際執行後，包含 runtime row count、warnings 等資訊。

也可用：

```sql
SET SHOWPLAN_XML ON;
GO
SELECT Id, CustomerId, InvoiceNo
FROM Orders
WHERE InvoiceNo = @invoiceNo;
GO
SET SHOWPLAN_XML OFF;
GO
```

每個 `SHOWPLAN_XML` 設定必須獨立成 batch；它只回傳 estimated plan，不執行查詢。要取得 actual plan，可使用會實際執行查詢的：

```sql
SET STATISTICS XML ON;
SELECT Id, CustomerId, InvoiceNo
FROM Orders
WHERE InvoiceNo = @invoiceNo;
SET STATISTICS XML OFF;
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

若使用 `STATISTICS IO/TIME`，常見欄位的意義是：`logical reads` 是從 data cache 讀的 pages，`physical reads` 是從磁碟讀的 pages，`read-ahead reads` 是預讀進 cache 的 pages；`CPU time` 與 `elapsed time` 是解析、編譯與執行耗時。沒有 SQL Server 實例時，不把未實測的數字寫成執行結果。

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
WHERE LOWER(InvoiceNo) = LOWER(@invoiceNo);
```

這不是絕對「一定 scan」的保證，但把 function / conversion 套在 indexed column 上，常讓 optimizer 難以直接用原始 key 定位。可改成 range predicate、建立適當 computed column / index，或在 schema 端統一資料。

### Implicit conversion

```sql
-- InvoiceNo 是 varchar(256)，但應用程式送出 nvarchar parameter。
WHERE InvoiceNo = @invoiceNo;
```

`varchar` 欄位配 `nvarchar` 參數時，SQL Server 會把欄位轉成 `nvarchar`；plan 會出現 `CONVERT_IMPLICIT`／PlanAffectingConvert，讓 seek 失效。EF Core 應用 `.IsUnicode(false)` 或 `HasColumnType("varchar(256)")`，ADO.NET 則使用 `SqlDbType.VarChar`。相反地，`int` 欄位配 `nvarchar` 參數時通常是參數被轉成 `int`，仍可 seek；非數字字串則會直接報 conversion error。

### 常見查詢問題

- `SELECT *`：多讀欄位、放大 network / memory、讓 covering index 更難設計。
- 不必要 JOIN：增加 row 數、join cost 與 duplicate risk。
- `%xxx`：前置 wildcard 通常無法從一般 B-tree 直接定位；`xxx%` 通常較有機會 seek。
- 不必要 `DISTINCT`：可能觸發 Sort / Hash Aggregate，且常是 JOIN / data model bug 的掩蓋。
- 大量 `OFFSET`：資料庫仍需跳過前面 rows；頁數越後面通常越昂貴，能用 keyset pagination 時改用 `WHERE Id > @lastId ORDER BY Id`。
- N+1：不是一條 query 的 plan，而是 application 產生大量 round trips。

### Parameter sniffing

SQL Server 可能在 stored procedure／parameterized query 編譯時使用當下參數值建立 plan；資料分布高度不均時，這個 plan 對另一個參數可能不理想。SQL Server 2022+ 的 Parameter Sensitive Plan optimization 需要資料庫 compatibility level 160，且目前只處理 equality predicate。仍應先用 actual plan、Query Store、statistics 與 workload 驗證；可比較 `OPTION (RECOMPILE)`、`OPTIMIZE FOR UNKNOWN` 與 `USE HINT('DISABLE_PARAMETER_SNIFFING')`，不要把其中一種當萬用解法。

## 5. 與 C# / EF Core 的關聯

```csharp
var query = db.Orders
    .TagWith("OrderList")
    .Where(order => order.CustomerId == customerId)
    .OrderBy(order => order.CreatedAt)
    .Select(order => new OrderListItem(order.OrderId, order.TotalAmount));

Console.WriteLine(query.ToQueryString());
var result = await query.ToListAsync(cancellationToken);
```

診斷步驟：

1. 先看 generated SQL，確認沒有過早 `ToList()`；`ToQueryString()` 只顯示將要執行的 SQL，不執行查詢。
2. 在 SSMS 用相同參數取得 actual plan；Query Store 用來看歷史 plan、平均 duration／logical reads，以及 plan 何時變更。
3. 比較 estimated / actual rows、logical reads、CPU、duration。
4. 檢查 index、statistics、data distribution 與 projection。
5. 修改後重新量測，不以單次本機結果代替正式環境負載。

`TagWith("OrderList")` 會在 SQL 前加 `-- OrderList`，方便把 LINQ 查詢和 log／Query Store 對回來；實際執行時間、參數與 `CommandExecuted` 事件要用 `LogTo`／`Microsoft.Extensions.Logging` 觀察。

```csharp
optionsBuilder.LogTo(Console.WriteLine, LogLevel.Information);
```

實際 log 會包含類似 `Microsoft.EntityFrameworkCore.Database.Command[20101] Executed DbCommand (Nms) ...` 的 command duration；敏感參數預設不記錄，只有在安全的測試環境才開 `EnableSensitiveDataLogging()`。

## 6. 常見誤區

- `NOLOCK` 不是正確效能修復，可能讀到 dirty / duplicated / missing rows。
- 一看到 scan 就加 index；先確認 query selectivity 與掃描是否其實合理。
- `ToQueryString()` 只協助看 SQL，不會顯示實際執行時間與完整 plan。

## 7. 面試回答

> 我會用 actual execution plan、logical reads、CPU、duration 和 estimated/actual rows 差異診斷，而不是只看 SQL 長短。常見問題包含不具 SARGability 的 function、implicit conversion、沒有合適 index、Key Lookup、Sort spill、N+1、過深 OFFSET 與 parameter sensitivity。Cost percentage 是 plan 內相對估算，不能直接當成實際時間百分比。

## 8. 小練習

1. 將 `WHERE CONVERT(date, CreatedAt) = @day` 改成 range predicate。
2. 為什麼 `SELECT *` 會讓 query performance 與 index design 更難？
3. 找一條 EF Core query，輸出 generated SQL 並比較 actual rows。
