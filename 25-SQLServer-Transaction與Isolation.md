---
title: 25 SQL Server Transaction 與 Isolation
tags: [sql-server, mssql, transaction, acid, isolation]
---

# 25 Transaction 與 Isolation Level

## 學習目標

- 用銀行轉帳理解 transaction 與 ACID。
- 能選擇與描述 SQL Server isolation level 的一致性與 concurrency trade-off。
- 在 ASP.NET Core / EF Core 中正確管理 transaction scope 與時間長度。

## 1. 一句話理解

Transaction 把多個資料庫操作包成一個不可分割的 logical unit；isolation level 再決定同時執行的 transaction 彼此能看到什麼、要等多久，以及允許哪些讀取異常。

## 2. 實際 SQL

```sql
BEGIN TRANSACTION;

BEGIN TRY
    UPDATE Accounts
    SET Balance = Balance - 50
    WHERE Id = 1 AND Balance >= 50;

    IF @@ROWCOUNT <> 1
        THROW 50001, 'Insufficient balance or account not found.', 1;

    UPDATE Accounts
    SET Balance = Balance + 50
    WHERE Id = 2;

    IF @@ROWCOUNT <> 1
        THROW 50002, 'Destination account not found.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
```

`SET XACT_ABORT ON` 常被用來讓 runtime error 自動終止 transaction：

```sql
SET XACT_ABORT ON;
```

仍要設計 error handling，不要以為所有錯誤狀態都能無條件再 rollback。

## 3. 執行結果

成功時：

```text
Account 1: 100 → 50
Account 2: 20  → 70
COMMIT：兩個 UPDATE 同時可見
```

中間失敗時：

```text
Account 1: 100 → 50  （暫時修改）
Account 2: UPDATE 失敗
ROLLBACK：Account 1 回到 100，Account 2 保持 20
```

ACID：

- Atomicity：全成功或全失敗。
- Consistency：transaction 前後滿足 constraint / invariant。
- Isolation：併發 transaction 的中間狀態不任意互相干擾。
- Durability：commit 後資料在系統故障下仍具持久性（具體程度依設定、storage 與 durability policy）。

## 4. SQL Server 背後大概做什麼

### Isolation level

| Level | Dirty read | Non-repeatable read | Phantom read | 直覺 |
| --- | --- | --- | --- | --- |
| `READ UNCOMMITTED` | 可能 | 可能 | 可能 | 最少保護；可讀未 commit 資料 |
| `READ COMMITTED` | 不允許 | 可能 | 可能 | SQL Server 傳統預設；讀鎖通常 statement 結束釋放；若 RCSI 開啟則用 row version |
| `REPEATABLE READ` | 不允許 | 不允許 | 可能 | 已讀 row 的 read lock 持續到 transaction 結束 |
| `SNAPSHOT` | 不允許 | 不允許 | 不允許 | 讀 transaction start 時的 row version；需啟用 snapshot isolation |
| `SERIALIZABLE` | 不允許 | 不允許 | 不允許 | 以 range lock 等方式提供最強隔離，可能大幅增加 blocking |

SQL Server 的 `READ_COMMITTED_SNAPSHOT` database option 會改變 `READ COMMITTED` 的讀取實作為 row versioning；要看環境設定，不能只背一張固定表。

### transaction 要短

持有 transaction 時，資料庫可能需要保留 lock / row version / log 資源。不要在 transaction 裡做外部 HTTP call、等待使用者輸入或執行大量不必要查詢；先準備好資料，再用最短範圍完成 atomic write。

## 5. 與 C# / EF Core 的關聯

```csharp
await using var transaction = await db.Database
    .BeginTransactionAsync(cancellationToken);

try
{
    var source = await db.Accounts
        .SingleAsync(x => x.Id == sourceId, cancellationToken);

    if (source.Balance < amount)
        throw new InvalidOperationException("Insufficient balance.");

    source.Balance -= amount;

    var destination = await db.Accounts
        .SingleAsync(x => x.Id == destinationId, cancellationToken);
    destination.Balance += amount;

    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

如果同一個 `DbContext` 的一次 `SaveChangesAsync` 會產生多個 command，EF Core provider 通常會在需要時使用 transaction；跨多個 `SaveChanges`、多個 context、或和其他 DB 操作組成一個 use case 時，需明確管理 transaction。不要把 database transaction 與 distributed transaction 混為一談。

## 6. 常見誤區

- transaction 不是只在 `INSERT` 使用；讀 + 判斷 + 寫也可能需要 atomicity / isolation。
- 提高 isolation level 不是免費的一致性；可能提高 lock、blocking、memory 與 deadlock。
- `READ UNCOMMITTED` / `NOLOCK` 可能讀到尚未 commit、重複或缺失資料，不能作為一般 read optimization。
- transaction 裡呼叫外部 API 會把 DB lock 綁在 network latency 上。
- EF Core `DbContext` lifetime、database transaction 與 HTTP request lifetime 要分開思考，通常 scoped context 不代表整個 request 必須是一個長 transaction。

## 7. 面試回答

> Transaction 用來保證一組操作符合 ACID；銀行轉帳中扣款和入款必須一起成功，否則 rollback。Isolation level 決定 concurrent transaction 可以看到什麼，從 READ UNCOMMITTED 到 SERIALIZABLE 是一致性與 concurrency 的 trade-off；SQL Server 預設 READ COMMITTED，但 RCSI 設定會影響讀取實作。實務上保持 transaction 短，不把外部 HTTP 或非必要工作放進去。

## 8. 小練習

1. 為銀行轉帳加入不足額時的 rollback。
2. 何時需要 `SNAPSHOT` 或 RCSI 而不是 `READ UNCOMMITTED`？
3. 解釋為什麼 transaction duration 會影響 blocking。
