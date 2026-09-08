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

EF Core 讓 C# model 與 schema 演進、把 tracked changes 保存回 SQL Server，但 concurrency、transaction、migration deployment 都是 production contract，不能只依賴 ORM magic。

## 2. 實際 SQL

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

若 `@@ROWCOUNT = 0`，代表資料已被別的 transaction 修改或不存在；這是 optimistic concurrency conflict。

### Migration commands

```bash
dotnet ef migrations add AddOrderTables
dotnet ef database update
dotnet ef migrations script --idempotent
dotnet ef migrations bundle --output artifacts/efbundle
```

Production 不應把「application 啟動時直接改 schema」當唯一 deployment strategy；migration SQL / bundle 應被 review、測試並由具備 schema permission 的 deployment identity 執行。

## 3. 執行結果

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

### Migration history

EF Core 會用 migration history table 記錄已套用 migration。Migration 本質上是 schema change code / SQL generation，可能包含 destructive operation；rename column 若被推斷成 drop + add，可能造成資料遺失，必須人工檢查 migration。

## 5. 與 C# / ASP.NET Core 的關聯

```csharp
try
{
    var user = await db.Users
        .SingleAsync(x => x.Id == id, cancellationToken);

    user.Name = request.Name;
    await db.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateConcurrencyException)
{
    return Conflict(new ProblemDetails
    {
        Title = "The user was modified by another request."
    });
}
```

Migration configuration：

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default")));
```

部署原則：

- application runtime identity 通常只需 CRUD permission，不應擁有任意 schema change permission。
- migration artifact 由 CI / deployment pipeline 產生與執行。
- production migration 先 review SQL、測試 rollback / data migration 與 large table impact。
- `EnsureCreated` 適合 prototype / test，不要和 migrations 混用期待它建立可演進的 production schema。

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
