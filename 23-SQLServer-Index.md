---
title: 23 SQL Server Index
tags: [sql-server, mssql, index, performance]
---

# 23 Index：SQL Server 如何更快找到資料

## 學習目標

- 建立 index 的資料結構模型。
- 分辨 clustered、nonclustered、composite、included、covering index。
- 能從 query predicate 推測 leading key、seek、scan 與 key lookup 的關係。

## 1. 一句話理解

Index 是依 key 排序的 B-tree：clustered index 就是 table 本身的排列，nonclustered index 是另外一份結構。它讓 SQL Server 先縮小範圍再定位資料；代價是儲存空間、寫入維護與 optimizer 選擇成本。

## 2. 實際 SQL

```sql
-- PRIMARY KEY 一定建 unique index；預設 clustered，已有 clustered index 時才變 nonclustered。
ALTER TABLE Orders
ADD CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (OrderId);

-- 常見查詢：依 CustomerId + Status 篩選，再取 OrderedAt / TotalAmount。
CREATE INDEX IX_Orders_CustomerId_Status
ON Orders (CustomerId, Status)
INCLUDE (OrderedAt, TotalAmount);

-- unique index 同時是 integrity constraint 的一部分。
CREATE UNIQUE INDEX UX_Orders_InvoiceNo
ON Orders (InvoiceNo);
```

## 3. 執行結果

以本節 DDL、clustered primary key 與 unique `InvoiceNo` index 為前提：

```sql
SELECT OrderId, CustomerId, InvoiceNo
FROM Orders
WHERE InvoiceNo = 'INV-2026-0001';
```

預期執行計畫（需在 SQL Server 以 `SET STATISTICS XML ON` 實測）：

- 沒有 `InvoiceNo` index：因為 table 有 clustered PK，運算子是 `Clustered Index Scan`；heap 才叫 `Table Scan`。
- 有 `UX_Orders_InvoiceNo`：先做 `Index Seek`；若還要取不在 index leaf 的欄位，會回 clustered index 做 `Key Lookup`。
- 若 index INCLUDE 了查詢需要的欄位，Key Lookup 可消失，形成 covering index。

`Seek` 表示按條件定位一段範圍；`Scan` 是把 index 或 table 從頭讀到尾。查詢需要大部分資料時，scan 可能比 seek 加大量 lookup 更合理。

## 4. SQL Server 背後大概做什麼

### Clustered index

- table data rows 依 clustered key 組織；leaf level 就是資料本身。
- 一張 table 最多一個 clustered index。
- Primary key 一定建立 unique index；預設是 clustered，table 已有 clustered index 時才用 nonclustered。沒有 PK 的 table 也可以有 clustered index。
- clustered key 會出現在 nonclustered index 的 row locator 中，因此 key 太寬會放大其他 index。
- 每張 table 最多 1 個 clustered index、最多 999 個 nonclustered index；index key 最多 32 欄，clustered key 上限 900 bytes、nonclustered key 上限 1,700 bytes（SQL Server 2016+）。

### Nonclustered index

- 是獨立的 B-tree，leaf level 保存 key、included columns 與 row locator。
- clustered table 的 row locator 是 clustered key，回表叫 Key Lookup；heap 的 row locator 是 RID，回表叫 RID Lookup。
- 可以有多個，但每個 INSERT / UPDATE / DELETE 都可能要維護它們。
- `INCLUDE` 欄位不參與排序，也不計入 key 欄位／key size 限制；最多 1,023 欄，用來補足投影、減少 Key Lookup。

### Composite index 與 leading key

```sql
CREATE INDEX IX_Orders_CustomerId_Status
ON Orders(CustomerId, Status);
```

它的索引順序先按 `CustomerId`，再按同一個 CustomerId 下的 `Status`。因此：

```sql
WHERE CustomerId = @customerId;                                  -- 使用 leading key
WHERE CustomerId = @customerId AND Status = @status;              -- 兩個 key 都可定位
WHERE Status = @status;                                           -- 沒有 CustomerId，不能沿同一範圍 seek
```

這個概念常稱為 leftmost／leading key；少了 `CustomerId`，optimizer 只能整個 index 掃過（Index Scan），不能沿著同一個 B-tree 範圍 seek。

欄位順序先放出現在 equality／inequality／BETWEEN 或 join 的欄位；equality 通常排在 range 前，再依 distinct 程度由高到低排列。排序需求與寫入成本再用實際查詢和 workload 驗證。

### Covering index

若 query 的 filter、join、sort 與 select 所需欄位都能從 index 取得，就可能不需要回 base table / clustered index，這稱為 covering query / covering index。它通常降低 lookup，但會增加 index storage 與寫入維護，不能對每個欄位都 INCLUDE。

## 5. 與 C# / EF Core 的關聯

Fluent API 建立 index：

```csharp
modelBuilder.Entity<Order>()
    .Property(x => x.InvoiceNo)
    .HasMaxLength(100);

modelBuilder.Entity<Order>()
    .HasIndex(x => new { x.CustomerId, x.Status })
    .IncludeProperties(x => new { x.OrderedAt, x.TotalAmount })
    .IsDescending(false, true);
```

EF Core 的 API 對應如下：

```csharp
modelBuilder.Entity<Order>()
    .HasIndex(x => x.InvoiceNo)
    .HasFilter("[InvoiceNo] IS NOT NULL")
    .IsUnique();
```

`IncludeProperties(...)` 建立 included columns，`IsDescending(...)` 自 EF Core 7 起可設定降冪 key，`HasFilter(...)` 建立 filtered index。SQL Server provider 對 nullable unique index 會自動加 `IS NOT NULL` filter；若要取消才傳 `HasFilter(null)`。

index 應從真正的 EF LINQ query 反推：`Where`、`Join`、`OrderBy`、projection 與 pagination 的組合，而不是從 entity 欄位列表挑選。

## 6. 常見誤區

- index 越多不等於越快；寫入、storage、cache memory 與 optimizer 選擇都要付成本。
- Primary key 不一定是 clustered index，clustered index 也不一定要是 primary key。
- `Index Seek` 不自動代表整個 query 很快；後面可能有大量 Key Lookup、Sort 或 join。
- `Index Scan` 不自動代表 query 很慢；查大量資料時 scan 可能合理。
- INCLUDE 欄位只解決 coverage，不會幫你建立 predicate 的排序與 seek 能力。
- 看到 missing index suggestion 不要直接全部建立；要和 workload、寫入成本、既有 index 重複性一起評估。

## 7. 面試回答

> Index 是依 key 排序的結構：clustered index 的 leaf 是資料本身，一張 table 最多一個；nonclustered index 是額外 B-tree，可用 INCLUDE 補投影欄位。Composite index 要注意 leading key，`(CustomerId, Status)` 對 CustomerId 開頭的條件有利，不代表單獨查 Status 也同樣有效。Index 會加速讀取，但會增加儲存空間與寫入維護。

## 8. 小練習

1. 為 `WHERE CustomerId = @id ORDER BY OrderedAt DESC` 設計一個 order index。
2. 解釋 `(CustomerId, Status)` 為什麼不等於 `(Status, CustomerId)`。
3. 什麼情況下 Key Lookup 可能成為效能瓶頸？
