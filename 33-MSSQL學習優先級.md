---
title: 33 MSSQL 學習優先級
tags: [sql-server, mssql, learning-plan, interview]
---

# 33 MSSQL / EF Core 學習優先級

版本基線：SQL Server 2022／compatibility level 160、EF Core 10、.NET 10。SQL Server 2019+、2025 的差異在條目旁標示；EF provider 的 `UseCompatibilityLevel` 不會改變實際資料庫 compatibility level。

## 學習目標

- 分辨 Backend developer 必須熟練的 SQL Server mental model 與 DBA 深度主題。
- 用面試與實務風險排序學習時間。
- 把 MSSQL 知識接回前面的 C# / ASP.NET Core 路線。

## 1. 一句話理解

目標不是成為 DBA，而是能寫出可維護、可觀測、在併發與大量資料下仍合理的 .NET Backend data access code。

## 2. 實際 SQL / C# 應會寫

```sql
-- A 級：應能自行寫並解釋
WITH Ranked AS
(
    SELECT o.Id, o.UserId, o.Status, o.CreatedAt,
        ROW_NUMBER() OVER
        (
            PARTITION BY UserId
            ORDER BY CreatedAt DESC, Id DESC
        ) AS rn
    FROM Orders o
)
SELECT Id, UserId, Status, CreatedAt
FROM Ranked
WHERE rn = 1;
```

```csharp
var orders = await db.Orders
    .Where(x => x.UserId == userId)
    .OrderByDescending(x => x.CreatedAt)
    .ThenByDescending(x => x.Id)
    .Take(50)
    .Select(x => new OrderDto(x.Id, x.Status, x.TotalAmount))
    .ToListAsync(cancellationToken);
```

## 3. 執行結果

### A：一定要會

- JOIN：INNER / LEFT、ON vs WHERE、1-to-many row multiplication。
- 資料型別與 mapping：`decimal(p,s)`、`varchar`／`nvarchar`、`datetime2`／`datetimeoffset`、`rowversion`、隱含轉換與 EF Core column configuration。
- 資料完整性：PRIMARY KEY、FOREIGN KEY、UNIQUE、CHECK、DEFAULT、NULLability；每個 constraint 都要能說出保護的 invariant。
- WHERE / GROUP BY / HAVING / ORDER BY 的 logical processing 直覺。
- NULL、`IS NULL`、三值邏輯、`ISNULL` / `COALESCE` / `NULLIF`。
- Subquery、CTE、derived table。
- Window function：`ROW_NUMBER`、`RANK`、`LAG`、`SUM OVER`、top-N-per-group。
- Clustered / nonclustered index、composite leading key、seek / scan 基礎。
- Estimated / actual execution plan 基礎與 estimated vs actual rows。
- Transaction、ACID、READ COMMITTED 與 RCSI 基本語意：RCSI OFF 時使用 shared lock，ON 時改用 statement-level row versioning；兩者仍可能有 nonrepeatable／phantom read。
- Lock / blocking / deadlock 基礎。
- `IEnumerable` vs `IQueryable`、`ToListAsync` execution boundary。
- SQL／LINQ 參數化：`FromSql`／`FromSqlInterpolated` 與 `FromSqlRaw` 的差異、`DbParameter`、SQL injection 邊界。
- LINQ translation boundary：top-level projection 的 client evaluation、其他位置翻譯失敗時的 runtime exception、用 `ToQueryString()`／logging 驗證 SQL。
- EF Core `DbContext` / `DbSet`、projection、`AsNoTracking`。
- EF Core N+1、`Include` / `ThenInclude`、`First` / `Single` / `Find`。
- `SaveChangesAsync`、migration 基本流程、rowversion optimistic concurrency。

### B：需要理解

- Covering index、Included columns、Key Lookup。
- SARGability、implicit conversion、`SELECT *`、unnecessary DISTINCT。
- Parameter sniffing / parameter-sensitive plans。
- SNAPSHOT（transaction-level versioning、需 `ALLOW_SNAPSHOT_ISOLATION ON`）、RCSI 與 row version store；傳統 version store 在 `tempdb`，SQL Server 2019+ 啟用 ADR 時可使用資料庫內 PVS。
- Optimistic vs pessimistic concurrency。
- `#TempTable` vs `@TableVariable` vs CTE。
- OFFSET/FETCH vs keyset pagination。
- Split query、cartesian explosion、identity resolution。
- Migration SQL review、idempotent scripts、bundle 與 deployment identity。
- Query Store、logical reads、memory grant、sort spill 的診斷方向。

### C：知道用途即可

- Partitioning strategy 的完整設計與 sliding window maintenance。
- Columnstore index、HTAP / analytical workload tuning。
- Indexed view 與 schema binding 細節。
- Service Broker、CDC / Change Tracking 的完整營運設計。
- Always On Availability Groups、replication、log shipping、HA / DR。
- Resource Governor、Query Store hint、advanced plan forcing。
- In-memory OLTP、memory-optimized table、native compiled procedure。
- CLR integration、SQL Server Agent job、SSIS / SSRS / SSAS。
- 深入 wait stats、latch contention、storage layout、filegroup / VLF 維運。

知道這些功能存在、能判斷何時找 DBA / platform engineer 即可；不要在尚未熟悉 A 級內容前花大量時間背全部設定細節。

## 4. SQL Server 背後大概做什麼

學習優先級的判斷標準：

```text
會不會改變 API correctness？
    ↓
會不會造成 N+1 / data corruption / deadlock？
    ↓
會不會影響 1 萬 → 1,000 萬筆的 scalability？
    ↓
面試是否常要求解釋 mental model？
    ↓
才決定是否深入 DBA-only feature
```

最值得練的不是孤立 SQL 題，而是「一條 endpoint 從 C# 到 SQL Server」：

```text
DTO input
 → validation
 → transaction / concurrency decision
 → IQueryable query shape
 → generated SQL
 → index / plan
 → lock / isolation
 → DTO output
```

## 5. 與 C# / EF Core 的關聯

建議每學一個 SQL Server 概念，都回答三個問題：

1. EF Core / LINQ 會產生什麼 query shape？
2. 應該使用 tracking、no-tracking、projection 還是 Include？
3. HTTP request cancellation、transaction lifetime 與 retry 怎麼處理？

例如學 deadlock 時，不只背 lock mode，還要能說：

```csharp
catch (SqlException ex) when (ex.Number == 1205)
{
    // 只記錄並交由外層以新 transaction 重跑整個工作單元；最後重新拋出。
    throw;
}
```

實際重試要包住完整 transaction delegate，設定最大次數、退避與 jitter；不能只重送其中一個 statement。commit 結果不明或寫入不可證明冪等時不可盲目重播。

## 6. 常見誤區

- 把 SQL Server 學成 syntax collection，卻不會看 execution plan。
- 把所有 read query 都加 `AsNoTracking`，卻不理解後續是否要更新 entity。
- 把所有效能問題歸咎於 index，忽略 N+1、payload、transaction duration 與 data distribution。
- 把所有一致性問題用 `SERIALIZABLE` 解決，造成 blocking / deadlock。
- 把 DBA-only feature 當成 Backend 面試的第一優先，忽略 NULL、JOIN、LINQ execution boundary 與 N+1。

## 7. 面試回答

> 我把 SQL Server 能力分三層：A 級是日常 backend 必須會的資料型別、constraints、參數化、query semantics、NULL、JOIN、CTE、window、index、execution plan、transaction、locking、EF Core translation 與 N+1；B 級是能協助定位問題的 parameter sniffing、covering index、snapshot、keyset pagination、migration deployment；C 級是知道用途、遇到專案需求再和 DBA 深入的 HA、partitioning、columnstore、advanced tuning。這樣先確保 correctness 和 production scalability。

## 8. 小練習

1. 把自己目前最常用的一條 API query 分類成 A / B / C 風險。
2. 為 `GET /users/{id}/orders` 寫一份 A 級檢查清單。
3. 列出三個你會先問 DBA / platform team，而不是自己猜設定的主題。

## 建議複習順序

1. [[18-SQLServer資料型別]] → [[19-SQLServer-NULL與三值邏輯]] → [[20-SQLServer-JOIN]]
2. [[21-SQLServer-Subquery-CTE-View]] → [[22-SQLServer-Window-Functions]]
3. [[23-SQLServer-Index]] → [[24-Execution-Plan與Query-Performance]]
4. [[25-SQLServer-Transaction與Isolation]] → [[26-SQLServer-Lock-Blocking-Deadlock]]
5. [[27-SQLServer-Programmability-Temp-Pagination]]
6. [[28-EF-Core-Query與Methods]] → [[29-EF-Core-Related-Data-Tracking-N+1]] → [[30-EF-Core-Concurrency與Migration]]
7. [[31-User-Order整合實作]] → [[32-MSSQL面試快速複習]]
