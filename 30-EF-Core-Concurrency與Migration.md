---
title: 30 EF Core Concurrency 與 Migration
tags: [ef-core, sql-server, concurrency, migration, schema]
---

# 30 EF Core：Concurrency、Transaction 與 Migration

## 學習目標

- 知道 EF Core 如何利用 concurrency token 偵測 lost update。
- 能在 use case 中選擇 transaction、rowversion 與 conflict response。
- 分清楚 Code First migrations 與 Database First reverse engineering。

## 1. 一句話理解

EF Core 能把模型與資料庫結構演進，也能把追蹤的變更保存回 SQL Server；正式環境仍要明確設計併發衝突、交易邊界與遷移部署。

## 2. 概念 SQL

### SQL Server `rowversion`

```sql
CREATE TABLE Users
(
    Id uniqueidentifier NOT NULL PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    Version rowversion NOT NULL
);
```

更新時概念上會包含原始 version：

```sql
UPDATE Users
SET Name = @newName
WHERE Id = @id
  AND Version = @originalVersion;
```

若 `@@ROWCOUNT = 0`，代表資料已被別的 transaction 修改或不存在；這是 optimistic concurrency conflict。以上是概念 SQL，不是 EF Core command log；實際 SQL Server provider 可能用 `OUTPUT INSERTED.Version` 取回新的 token。

### Migration commands

```bash
dotnet ef migrations add AddOrderTables
dotnet ef database update
dotnet ef migrations script --idempotent
dotnet ef migrations bundle --output artifacts/efbundle
```

Production 不應把「application 啟動時直接改 schema」當唯一 deployment strategy；migration SQL / bundle 應被 review、測試並由具備 schema permission 的 deployment identity 執行。

## 3. 衝突時序（可重現情境）

### Optimistic concurrency timeline

```text
Request A 讀取 User.Name = Ada, Version = v1
Request B 讀取 User.Name = Ada, Version = v1
Request A UPDATE ... WHERE Version = v1 → 1 row, Version = v2
Request B UPDATE ... WHERE Version = v1 → 0 rows
EF Core → DbUpdateConcurrencyException
```

API 可以把 conflict 轉成 `409 Conflict`，或依 domain 規則 reload、merge、retry；不要默默覆蓋別人的更新。

### Code First / Database First

| 模式 | Source of truth | 典型做法 |
| --- | --- | --- |
| Code First | EF model / code | 修改 entity / Fluent config，產生 migration，部署 schema |
| Database First | existing DB schema | reverse engineer scaffold `DbContext` 與 entity，DB 由 DBA / schema pipeline 管理 |

## 4. SQL Server 背後大概做什麼

### EF Core concurrency token

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

EF Core 會把讀回的 token 放進 tracked original values，`SaveChangesAsync` 時加入 WHERE predicate。這不等於 pessimistic locking；讀取時通常不先把 row 鎖住等候其他 request。

Web API 必須把讀取時的原始 token 傳回 client：

```csharp
public sealed record UserResponse(
    Guid Id,
    string Name,
    string Version); // rowversion 以 Base64 傳輸

public sealed record UpdateUserRequest(
    Guid Id,
    string Name,
    string Version);
```

更新時把 request 的原始 token 設成 EF original value，而不是拿重新查到的 current value：

```csharp
var user = await db.Users.SingleOrDefaultAsync(
    x => x.Id == request.Id, cancellationToken);

if (user is null)
    return NotFound();

db.Entry(user).Property(x => x.Version).OriginalValue =
    Convert.FromBase64String(request.Version);
user.Name = request.Name;

try
{
    await db.SaveChangesAsync(cancellationToken);
    return NoContent();
}
catch (DbUpdateConcurrencyException)
{
    return Conflict(new ProblemDetails
    {
        Title = "The user was modified by another request."
    });
}
```

兩個 client 都以 v1 讀取時，先送出的更新得到 v2；另一個 client 帶 stale v1 送出時必定得到 409，不會覆蓋第一個更新。Malformed Base64 應回 400，找不到 user 回 404，未預期例外回 500。

### Transaction 與 isolation

一次 `SaveChanges` 預設在同一 transaction 內全成功或全不寫；跨多個 `SaveChanges` 或多個 context 時，明確建立交易：

```csharp
using System.Data;

await using var transaction = await db.Database
    .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

// 多個 query / SaveChanges 組成同一個 use case。
await transaction.CommitAsync(cancellationToken);
```

`ReadCommitted` 可能讓同一 transaction 的兩次讀取看到不同值；`RepeatableRead` 以鎖定方式保護已讀 row，`Snapshot` 使用版本且可能產生 serialization/update conflict，`Serializable` 也保護範圍但會增加 blocking。交易要短，不能跨使用者編輯時間。Execution strategy retry 時要把整個 transaction delegate 重播；SQL Server MARS 啟用時 EF Core 不會建立 savepoint。

### Migration history

EF Core 會用 migration history table 記錄已套用 migration。Migration 本質上是 schema change code / SQL generation，可能包含 destructive operation；rename column 若被推斷成 drop + add，可能造成資料遺失，必須人工檢查 migration。

## 5. 與 C# / ASP.NET Core 的關聯

```csharp
// Update endpoint 使用上方的 request.Version，設定 EF original value，
// SaveChanges 後將 DbUpdateConcurrencyException 對應為 409。
```

DbContext 與 SQL Server provider 設定：

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default")));
```

部署策略：

| 策略 | 用途 | 限制 |
| --- | --- | --- |
| reviewed SQL script | DBA／人工審查、修改、封存後執行 | SQL script 不使用 EF9+ migration lock，部署流程要自行保證單一執行者 |
| migration bundle | 自動化 deployment job | bundle 目前不能直接列出內含 migration 或檢視將執行的 SQL；以獨立 migration identity 和 secret provider 執行 |
| `dotnet ef database update` | local development／testing | 不適合管理 production database |

runtime identity 只需 CRUD permission，不應擁有 schema change permission；`EnsureCreated` 適合 prototype／test，不要和 migrations 混用期待它建立可演進的 production schema。EF Core 9 的 migration transaction 行為在 EF Core 10 已撤回，版本與部署策略要一起固定。

## 6. 常見誤區

- `rowversion` 不是 datetime，也不是 application 自己傳入的 timestamp。
- optimistic concurrency 不會自動替你決定 conflict resolution policy。
- `Update(entity)` 不等於 concurrency-safe update；要配置 token 並檢查 conflict。
- `dotnet ef database update` 很方便，但 production 需要可審查、可重現的 deployment strategy。
- Migration file 能編譯不代表資料轉換安全；rename / split / backfill 要人工設計。

## 7. 面試回答

> EF Core optimistic concurrency 會把 concurrency token，例如 SQL Server `rowversion`，在 query 時讀入、在 SaveChanges 時放進 UPDATE / DELETE 的 WHERE。若受影響 row 數是 0，EF Core 拋 `DbUpdateConcurrencyException`，application 應轉 409、reload/merge 或依 policy retry。Migration 是 schema evolution，不只是執行 `Add-Migration`；production 要 review generated SQL，並由 deployment identity 套用。

## 8. 小練習

1. 為 User entity 加上 `rowversion` concurrency token。
2. 設計 API 遇到 `DbUpdateConcurrencyException` 時的 response。
3. 說明 Code First 與 Database First 的 source of truth。
