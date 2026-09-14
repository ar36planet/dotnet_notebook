---
title: 26 SQL Server Lock Blocking Deadlock Concurrency
tags: [sql-server, mssql, lock, blocking, deadlock, concurrency]
---

# 26 Lock、Blocking、Deadlock 與 Concurrency

## 學習目標

- 分辨 lock、blocking 與 deadlock。
- 能畫出兩個 transaction 互相等待的 deadlock cycle。
- 用 optimistic / pessimistic concurrency 思維設計 ASP.NET Core update。

## 1. 一句話理解

Lock 是資料庫保護 shared state 的機制；blocking 是一個 transaction 等另一個釋放衝突 lock；deadlock 是等待形成 cycle，SQL Server 必須選一個 victim rollback。

## 2. 實際 SQL

### Lock mode 的直覺

```text
Shared (S)：讀取資料；多個 reader 通常可以共存
Exclusive (X)：修改資料；與其他衝突 lock 互斥
Update (U)：準備更新；與 S 相容，但同一資源一次只能有一個 U，降低 S→X conversion deadlock
Intent：表示打算在下層資源取得 S／U／X，例如 IS、IU、IX、SIX
```

用兩個 SSMS sessions 示意 blocking（先在測試 database 執行一次）：

```sql
IF OBJECT_ID('dbo.Accounts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Accounts
    (
        Id int NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
        DisplayName varchar(100) NOT NULL,
        Balance decimal(18,2) NOT NULL,
        Version rowversion NOT NULL
    );

    INSERT dbo.Accounts (Id, DisplayName, Balance)
    VALUES (1, 'Ada', 100.00), (2, 'Grace', 100.00);
END;
```

```sql
-- 前置條件：Accounts(Id int PRIMARY KEY, DisplayName varchar(100), Balance decimal(18,2))
-- Session A
BEGIN TRANSACTION;
UPDATE Accounts SET DisplayName = 'Ada v2' WHERE Id = 1;
-- 不要先 COMMIT
```

```sql
-- Session B：locking READ COMMITTED 下等待 A 的 X lock
SELECT * FROM Accounts WITH (READCOMMITTEDLOCK) WHERE Id = 1;
```

## 3. 執行結果

### Deadlock cycle

```text
Transaction A                         Transaction B
--------------                        --------------
UPDATE User 1 取得 X lock             UPDATE User 2 取得 X lock
等待 User 2                           等待 User 1
        └──────────── cycle ──────────────┘
```

SQL Server 會偵測循環，選擇一個 deadlock victim，回傳 error 1205；另一個 transaction 才能繼續。這類死結通常來自應用程式的資源存取順序或交易範圍設計。

SQL Server 2025／部分 Azure SQL 可啟用 optimized locking，row／page X lock 的生命週期與 DMV wait type 可能不同；診斷時先查 `is_optimized_locking_on` 與 `is_read_committed_snapshot_on`。

可重現的兩個 session 順序：

```sql
-- Session A
SET DEADLOCK_PRIORITY LOW;
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = Balance - 10 WHERE Id = 1;
WAITFOR DELAY '00:00:05';
UPDATE dbo.Accounts SET Balance = Balance - 10 WHERE Id = 2;
ROLLBACK;
```

```sql
-- Session B：和 A 相反的鎖定順序
SET DEADLOCK_PRIORITY HIGH;
BEGIN TRANSACTION;
UPDATE dbo.Accounts SET Balance = Balance - 10 WHERE Id = 2;
WAITFOR DELAY '00:00:05';
UPDATE dbo.Accounts SET Balance = Balance - 10 WHERE Id = 1;
ROLLBACK;
```

預期錯誤（需在 SQL Server 執行後核對）：

```text
Error 1205: Transaction (Process ID ...) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.
```

`DEADLOCK_PRIORITY` 先決定 victim；同優先權時 SQL Server 會比較回滾成本。priority 只改重要性，不會消除死結。

## 4. 診斷目前的封鎖

診斷方向：

```sql
SELECT
    r.session_id,
    r.blocking_session_id,
    r.wait_type,
    r.wait_time,
    r.status,
    t.text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0;
```

常見降低 blocking / deadlock 的策略：

- 保持 transaction 短，先做非 DB 工作。
- 所有 code path 以一致順序存取資源，例如都先 User 再 Order。
- 讓 predicate 有合理 index，避免為找一筆 row 掃大量資料並鎖更多資源。
- 避免不必要的 `SERIALIZABLE`、`HOLDLOCK` 與過度寬的 lock hint。
- 讀 workload 可評估 row-versioning isolation（RCSI / SNAPSHOT），但先理解 tempdb、consistency 與 update conflict。
- 辨識 SQL Server 1205 後，建立新 transaction、重跑整個資料庫工作單元；限制次數並使用 exponential backoff + jitter，資料庫外副作用要有 idempotency／outbox。

RCSI 與 SNAPSHOT 不同：RCSI 是 statement-start snapshot，`READ COMMITTED` 讀取通常不等 writer；SNAPSHOT 是 transaction-start snapshot，需要 `ALLOW_SNAPSHOT_ISOLATION ON`，寫入衝突可能得到 error 3960。兩者都不會取消資料修改所需的 locks。

DMV 只能看目前的等待；死結發生後應查 `system_health` Extended Events 的 `xml_deadlock_report`，讀取 `victim-list`、`process-list`、`resource-list`、`priority` 與 `logused`。`sys.dm_exec_requests` 查詢也要搭配 `sys.dm_exec_sessions`／`sys.dm_exec_input_buffer` 找 sleeping head blocker，不能只看被擋的 request。

## 5. 與 C# / EF Core 的關聯

### Optimistic concurrency：`rowversion`

SQL Server：

```sql
-- Version 是既有 Accounts schema 的唯一 rowversion 欄位。
SELECT Id, DisplayName, Balance, Version
FROM Accounts;
```

EF Core：

```csharp
public sealed class User
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public byte[] Version { get; set; } = [];
}

modelBuilder.Entity<User>()
    .Property(x => x.Version)
    .IsRowVersion();
```

兩個 request 都讀到同一個 Version，第一個先 Save 成功，第二個 `SaveChangesAsync` 因 WHERE version 不再匹配而拋出 `DbUpdateConcurrencyException`。這是 optimistic concurrency：先不鎖住等待，衝突發生再處理。

### Pessimistic concurrency

鎖定式悲觀控制要在短 transaction 內使用 `REPEATABLE READ`／`SERIALIZABLE`，或對必要查詢使用 `UPDLOCK`（必要時搭配 `HOLDLOCK`／範圍索引）讓讀到的 row 在更新前保持保護。單純 `BEGIN TRANSACTION` 加預設 `READ COMMITTED` 不會保留一般 SELECT 的 shared lock；`SNAPSHOT` 則是 row-versioned optimistic control，不是悲觀鎖。這些策略會增加 blocking／deadlock 風險。

```sql
BEGIN TRANSACTION;
SELECT Id, Balance
FROM dbo.Accounts WITH (UPDLOCK, HOLDLOCK)
WHERE Id = 1;
-- 在同一 transaction 內檢查餘額並 UPDATE
COMMIT;
```

```csharp
await db.SaveChangesAsync(cancellationToken);
```

`DbUpdateConcurrencyException` 不是 deadlock 1205；它表示 rowversion 原始值不再匹配。API 應在 exception mapping 層回傳 409，或重新載入 database values 後依領域規則合併，不要把它當成可立即無條件重試的 deadlock。

## 6. 常見誤區

- blocking 是等待；deadlock 是互相等待形成 cycle，兩者不是同義詞。
- Deadlock retry 是 resilience，不是修正 lock ordering / transaction duration 的替代品。
- `rowversion` 是 binary concurrency token，不是日期時間。
- optimistic concurrency 不代表完全沒有 lock；它是 update conflict handling strategy。
- 只看 application exception 不足以診斷 deadlock，應看 deadlock graph、blocked sessions、query plan 與 transaction scope；1205 的 retry 要重跑整個 transaction，`DbUpdateConcurrencyException` 則是 rowversion 衝突。

## 7. 面試回答

> Blocking 是 transaction 因為另一個衝突 lock 而等待；deadlock 是兩個或更多 transaction 互相等待形成 cycle，SQL Server 會選 victim rollback 並回 error 1205。改善方式包括縮短 transaction、一致的資源存取順序、合理 index、降低不必要 isolation，以及對可安全重試的操作做有限 retry。EF Core 常用 `rowversion` 做 optimistic concurrency，衝突時處理 `DbUpdateConcurrencyException` 並可回傳 409。

## 8. 小練習

1. 以 User 1 / User 2 畫出 deadlock cycle。
2. 設計一個 user update 的 `rowversion` conflict response。
3. 列出三個降低 transaction blocking 的方法。
