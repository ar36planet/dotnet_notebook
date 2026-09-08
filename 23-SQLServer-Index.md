---
title: 23 SQL Server Index
tags: [sql-server, mssql, index, performance]
---

# 23 Index：SQL Server 如何更快找到資料

## 學習目標

- 建立 index 的資料結構 mental model。
- 分辨 clustered、nonclustered、composite、included、covering index。
- 能從 query predicate 推測 leading key、seek、scan 與 key lookup 的關係。

## 1. 一句話理解

Index 是一份依 key 組織的額外資料結構，讓 SQL Server 先縮小要找的範圍，再定位到資料，而不是每次從整張 table 逐列檢查；代價是額外 storage、寫入維護與 optimizer 選擇成本。

## 2. 實際 SQL

```sql
-- Primary key 通常會建立 unique index；clustered / nonclustered 需明確決定。
ALTER TABLE Users
ADD CONSTRAINT PK_Users PRIMARY KEY CLUSTERED (Id);

-- 常見查詢：依 Name + Status 篩選，再取 CreatedAt / Email。
CREATE INDEX IX_Users_Name_Status
ON Users (Name, Status)
INCLUDE (CreatedAt, Email);

-- unique index 同時是 integrity constraint 的一部分。
CREATE UNIQUE INDEX UX_Users_Email
ON Users (Email);
```

## 3. 執行結果

假設有一百萬筆 Users：

```sql
SELECT Id, Name, Email
FROM Users
WHERE Email = 'ada@example.com';
```

- 沒有 Email index：可能需要 Table Scan，檢查大量 row。
- 有可用的 Email index：可能從 B-tree root → branch → leaf 做 Index Seek，再取出 row。
- 若 index leaf 已包含查詢需要的欄位：可能直接完成，形成 covering index。
- 若 index 只找到 key / row locator，其他欄位還要回 clustered index 找：可能出現 Key Lookup。

`Seek` 通常表示按條件定位一段範圍；`Scan` 表示讀取 index / table 的大量或全部部分。Scan 不一定永遠錯，例如查詢需要 80% rows 時，scan 可能比 seek + 大量 lookup 更合理。

## 4. SQL Server 背後大概做什麼

### Clustered index

- table data rows 依 clustered key 組織；leaf level 就是資料本身。
- 一張 table 最多一個 clustered index。
- Primary key constraint 預設常建立 clustered index，但可以指定成 nonclustered，也可以讓沒有 PK 的 table 有 clustered index。
- clustered key 會出現在 nonclustered index 的 row locator 中，因此 key 太寬會放大其他 index。

### Nonclustered index

- 是獨立的 B-tree，leaf level 保存 key、included columns 與 row locator。
- 可以有多個，但每個 INSERT / UPDATE / DELETE 都可能要維護它們。
- `INCLUDE` 欄位不參與排序，也不計入 key size 的同一種限制；它們用來補足 projection，減少 Key Lookup。

### Composite index 與 leading key

```sql
CREATE INDEX IX_User_Name_Status
ON Users(Name, Status);
```

它的索引順序先按 `Name`，再按同一個 Name 下的 `Status`。因此：

```sql
WHERE Name = @name;                         -- 可能有效使用 leading key
WHERE Name = @name AND Status = @status;   -- 通常更精準
WHERE Status = @status;                    -- 沒有 Name，未必能有效 seek
```

這個概念常被稱為 leftmost / leading key；不是說 `Status` 完全不能被 optimizer 使用，而是缺少前導欄位時通常不能沿著同樣的 B-tree 範圍直接定位。

column order 的決策要看：常見 equality predicate、range predicate、join key、排序需求、selectivity、資料分布與 write cost，不是只把「最常出現的欄位」放第一。

### Covering index

若 query 的 filter、join、sort 與 select 所需欄位都能從 index 取得，就可能不需要回 base table / clustered index，這稱為 covering query / covering index。它通常降低 lookup，但會增加 index storage 與寫入維護，不能對每個欄位都 INCLUDE。

## 5. 與 C# / EF Core 的關聯

Fluent API 建立 index：

```csharp
modelBuilder.Entity<User>()
    .HasIndex(x => new { x.Name, x.Status })
    .IncludeProperties(x => new { x.CreatedAt, x.Email });
```

實際 provider / EF Core version 對 filtered index、included columns、descending index 等能力要查文件並檢查 migration：

```csharp
modelBuilder.Entity<User>()
    .HasIndex(x => x.Email)
    .IsUnique();
```

index 設計應從真正的 EF LINQ query 出發：`Where`、`Join`、`OrderBy`、projection 與 pagination 的組合，比單看 entity 欄位更有意義。

## 6. 常見誤區

- index 越多不等於越快；寫入、storage、cache memory 與 optimizer 選擇都要付成本。
- Primary key 不一定是 clustered index，clustered index 也不一定要是 primary key。
- `Index Seek` 不自動代表整個 query 很快；後面可能有大量 Key Lookup、Sort 或 join。
- `Index Scan` 不自動代表 query 很慢；查大量資料時 scan 可能合理。
- INCLUDE 欄位只解決 coverage，不會幫你建立 predicate 的排序與 seek 能力。
- 短期看到 missing index suggestion 不要直接全部建立；要和 workload、寫入成本、既有 index 重複性一起評估。

## 7. 面試回答

> Index 是依 key 排序的額外結構，讓 SQL Server 可以先定位範圍，而不是掃整張 table。Clustered index 的 leaf 是資料本身，一張 table 最多一個；nonclustered index 是額外 B-tree，可用 INCLUDE 補 projection 欄位。Composite index 要注意 leading key，`(Name, Status)` 通常對 Name 開頭的 predicate 有利，不代表單獨查 Status 也同樣有效。Index 會加速 read，但會增加 storage 與 write maintenance。

## 8. 小練習

1. 為 `WHERE UserId = @id ORDER BY CreatedAt DESC` 設計一個 order index。
2. 解釋 `(Name, Status)` 為什麼不等於 `(Status, Name)`。
3. 什麼情況下 Key Lookup 可能成為效能瓶頸？
