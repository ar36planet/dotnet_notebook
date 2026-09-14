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

`SET XACT_ABORT ON` 會讓多數 runtime error 使 transaction 進入不可提交狀態；CATCH 仍要依 `XACT_STATE()` 決定是否完整回滾：

```sql
SET XACT_ABORT ON;
```

`XACT_STATE() = 0` 代表沒有 transaction，不能 rollback；`1` 代表可提交；`-1` 代表 uncommittable，只能完整 `ROLLBACK`，不能 `COMMIT` 或回到 savepoint。`XACT_ABORT` 預設是 OFF；OFF 時 constraint error 可能只回滾失敗的 statement，後續 `COMMIT` 仍提交其他變更。

## 3. 執行結果

預期成功結果（需在 SQL Server 執行後核對）：

```text
Account 1: 100 → 50
Account 2: 20  → 70
COMMIT：兩個 UPDATE 同時可見
```

預期中間失敗結果：

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
| `READ COMMITTED` | 不允許 | 可能 | 可能 | 地端 SQL Server 預設；shared lock 讀完 row／page 就釋放，最晚到 statement 結束；RCSI 開啟後改用 row version |
| `REPEATABLE READ` | 不允許 | 不允許 | 可能 | 已讀 row 的 read lock 持續到 transaction 結束 |
| `SNAPSHOT` | 不允許 | 不允許 | 不允許 | 第一次存取資料時的 row version；需 `ALLOW_SNAPSHOT_ISOLATION ON` |
| `SERIALIZABLE` | 不允許 | 不允許 | 不允許 | 以 range lock 等方式提供最強隔離，可能大幅增加 blocking |

地端 SQL Server 的 `READ_COMMITTED_SNAPSHOT` 預設 OFF，Azure SQL Database 預設 ON；開啟後 `READ COMMITTED` 讀 statement 開始時的 row version，不再拿 shared lock，但 non-repeatable／phantom read 仍可能發生。

### transaction 要短

持有 transaction 時，資料庫可能需要保留 lock / row version / log 資源。不要在 transaction 裡做外部 HTTP call、等待使用者輸入或執行大量不必要查詢；先準備好資料，再用最短範圍完成 atomic write。

SQL Server 預設是 autocommit mode，每個 statement 完成時獨立提交或回滾；`BEGIN TRAN`／`COMMIT` 是 explicit transaction，`SET IMPLICIT_TRANSACTIONS ON` 則由下一個 statement 自動開啟。`@@TRANCOUNT` 只計算巢狀 transaction 層數：內層 `COMMIT` 只減一，`ROLLBACK` 卻會回滾整個 transaction；它不能判斷交易是否 uncommittable，這要看 `XACT_STATE()`。

## 5. 與 C# / EF Core 的關聯

若兩個 transaction 都先讀到餘額 100、各自判斷可以扣 50，再用 EF tracked entity 寫回絕對值 50，最後一個 `UPDATE` 會覆蓋前一個，餘額錯在 50。轉帳 invariant 必須由同一個 transaction 與隔離策略保護。

```csharp
using System.Data;

await using var transaction = await db.Database
    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

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
```

`await using` 在例外或取消時 dispose transaction，未 commit 的交易會回滾；若要自行 catch，rollback 使用 `CancellationToken.None`，避免原本就是 cancellation 時 rollback 再次拋例外。`Serializable` 會增加 blocking／deadlock 成本，正式策略要配合重試與固定鎖定順序。

一次 `SaveChanges` 預設在同一個 transaction 內全成功或全不寫；`AutoTransactionBehavior.WhenNeeded` 只是在單一 statement 已具原子性時省掉額外 BEGIN／COMMIT。跨多個 `SaveChanges`、多個 context、或和其他 DB 操作組成一個使用情境時，需明確管理 transaction。

跨 context 可共用同一個 `DbTransaction`；若使用 `TransactionScope`，非同步程式碼要傳 `TransactionScopeAsyncFlowOption.Enabled`。分散式 transaction 在 .NET 7+ 只支援 Windows，不能把它當成跨平台預設方案。

## 6. 常見誤區

- transaction 不是只在 `INSERT` 使用；讀 + 判斷 + 寫也可能需要 atomicity / isolation。
- 提高 isolation level 會增加 lock 持有時間、blocking、版本存放與 deadlock 機率。
- `READ UNCOMMITTED` / `NOLOCK` 可能讀到尚未 commit、重複或缺失資料，不能作為一般 read optimization。
- transaction 裡呼叫外部 API 會把 DB lock 綁在 network latency 上。
- EF Core `DbContext` lifetime、database transaction 與 HTTP request lifetime 要分開思考，通常 scoped context 不代表整個 request 必須是一個長 transaction。

## 7. 面試回答

> Transaction 用來保證一組操作符合 ACID；銀行轉帳中扣款和入款必須一起成功，否則 rollback。Isolation level 決定 concurrent transaction 可以看到什麼，從 READ UNCOMMITTED 到 SERIALIZABLE 是一致性與 concurrency 的 trade-off；SQL Server 預設 READ COMMITTED，但 RCSI 設定會影響讀取實作。實務上保持 transaction 短，不把外部 HTTP 或非必要工作放進去。

## 8. 小練習

1. 為銀行轉帳加入不足額時的 rollback。
2. 何時需要 `SNAPSHOT` 或 RCSI 而不是 `READ UNCOMMITTED`？
3. 解釋為什麼 transaction duration 會影響 blocking。
