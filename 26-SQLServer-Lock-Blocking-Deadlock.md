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
Update (U)：準備更新，減少 read-then-update 的部分 deadlock
Intent：表示較大層級資源內有 row / page lock
```

用兩個 SSMS sessions 示意 blocking：

```sql
-- Session A
BEGIN TRANSACTION;
UPDATE Users SET Name = 'Ada v2' WHERE Id = 1;
-- 不要先 COMMIT
```

```sql
-- Session B：可能等待 A 的 X lock
SELECT * FROM Users WHERE Id = 1;
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

SQL Server 會偵測 cycle，選擇一個 deadlock victim，通常回傳 error 1205；另一個 transaction 才能繼續。這不是「SQL Server 壞掉」，而是 application 的 lock ordering / transaction design 產生了 concurrency conflict。

## 4. SQL Server 背後大概做什麼

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
- 對 deadlock error 做有限次、有 jitter 的 retry；retry 不應掩蓋 root cause，也要確認 operation 是否可安全重試。

## 5. 與 C# / EF Core 的關聯

### Optimistic concurrency：`rowversion`

SQL Server：

```sql
ALTER TABLE Users ADD Version rowversion NOT NULL;
```

EF Core：

```csharp
public sealed class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[] Version { get; set; } = [];
}

modelBuilder.Entity<User>()
    .Property(x => x.Version)
    .IsRowVersion();
```

兩個 request 都讀到同一個 Version，第一個先 Save 成功，第二個 `SaveChangesAsync` 因 WHERE version 不再匹配而拋出 `DbUpdateConcurrencyException`。這是 optimistic concurrency：先不鎖住等待，衝突發生再處理。

### Pessimistic concurrency

透過較高 isolation、transaction 或特定 locking semantics 先保護資料，讓其他 request 等待。它可以避免某些 race，但增加 blocking 與 deadlock 風險；不要只為了「不想處理 conflict」就到處使用。

```csharp
try
{
    await db.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateConcurrencyException)
{
    // 回傳 409 Conflict、重新載入、merge 或依 domain policy 拒絕。
    throw;
}
```

## 6. 常見誤區

- blocking 是等待；deadlock 是互相等待形成 cycle，兩者不是同義詞。
- Deadlock retry 是 resilience，不是修正 lock ordering / transaction duration 的替代品。
- `rowversion` 是 binary concurrency token，不是日期時間。
- optimistic concurrency 不代表完全沒有 lock；它是 update conflict handling strategy。
- 只看 application exception 不足以診斷 deadlock，應看 deadlock graph、blocked sessions、query plan 與 transaction scope。

## 7. 面試回答

> Blocking 是 transaction 因為另一個衝突 lock 而等待；deadlock 是兩個或更多 transaction 互相等待形成 cycle，SQL Server 會選 victim rollback 並回 error 1205。改善方式包括縮短 transaction、一致的資源存取順序、合理 index、降低不必要 isolation，以及對可安全重試的操作做有限 retry。EF Core 常用 `rowversion` 做 optimistic concurrency，衝突時處理 `DbUpdateConcurrencyException` 並可回傳 409。

## 8. 小練習

1. 以 User 1 / User 2 畫出 deadlock cycle。
2. 設計一個 user update 的 `rowversion` conflict response。
3. 列出三個降低 transaction blocking 的方法。
