---
title: 31 User Order SQL Server EF Core 整合實作
tags: [sql-server, ef-core, aspnet-core, web-api, capstone]
---

# 31 User / Order 整合實作

## 學習目標

- 把 SQL Server schema、index、EF Core model 與 ASP.NET Core endpoint 串起來。
- 用 projection 避免 N+1，用 transaction 保護 order 建立流程。
- 能從 1 萬筆資料推演到 1,000 萬筆資料時的風險。

## 1. 一句話理解

這個小系統以 `Users → Orders → OrderItems` 為主線，示範 schema constraint、query shape、EF tracking、transaction、DTO、DI、async 與 pagination 如何互相影響。

## 2. 實際 SQL

### Schema

```sql
CREATE TABLE dbo.Users
(
    Id uniqueidentifier NOT NULL
        CONSTRAINT PK_Users PRIMARY KEY CLUSTERED,
    Email nvarchar(320) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_Users_IsActive DEFAULT (1),
    CreatedAt datetimeoffset(7) NOT NULL,
    Version rowversion NOT NULL
);

CREATE UNIQUE INDEX UX_Users_Email
ON dbo.Users(Email);

CREATE TABLE dbo.Orders
(
    Id bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED,
    UserId uniqueidentifier NOT NULL,
    Status varchar(20) NOT NULL,
    TotalAmount decimal(19,4) NOT NULL,
    CreatedAt datetimeoffset(7) NOT NULL,
    CONSTRAINT FK_Orders_Users
        FOREIGN KEY (UserId) REFERENCES dbo.Users(Id)
);

CREATE INDEX IX_Orders_UserId_CreatedAt
ON dbo.Orders(UserId, CreatedAt DESC, Id DESC)
INCLUDE (Status, TotalAmount);

CREATE TABLE dbo.OrderItems
(
    Id bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_OrderItems PRIMARY KEY CLUSTERED,
    OrderId bigint NOT NULL,
    ProductCode varchar(50) NOT NULL,
    Quantity int NOT NULL,
    UnitPrice decimal(19,4) NOT NULL,
    CONSTRAINT CK_OrderItems_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_OrderItems_UnitPrice CHECK (UnitPrice >= 0),
    CONSTRAINT FK_OrderItems_Orders
        FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id)
);

CREATE INDEX IX_OrderItems_OrderId
ON dbo.OrderItems(OrderId)
INCLUDE (ProductCode, Quantity, UnitPrice);
```

完整 schema script 放在 [examples/UserOrderSql/schema.sql](/Users/air.forest/Desktop/GitLab/dotnet_notebook/examples/UserOrderSql/schema.sql)。

## 3. 執行結果

### API contract

```text
GET  /api/users
GET  /api/users/{id}
GET  /api/users/{id}/orders?pageSize=50&beforeCreatedAt=...&beforeId=...
POST /api/orders
```

`GET /api/users/{id}/orders` 回傳 DTO，而不是整個 entity graph：

```json
[
  {
    "id": 101,
    "status": "Paid",
    "totalAmount": 1280.50,
    "createdAt": "2026-09-08T10:30:00+08:00",
    "items": [
      {
        "productCode": "BOOK-001",
        "quantity": 2,
        "unitPrice": 640.25
      }
    ]
  }
]
```

### 預期 query shape

```text
GET /api/users/{id}/orders
    → 1 個 parameterized SQL query
    → Orders filtered by UserId
    → projection OrderDto + nested ItemDto
    → ToListAsync
    → JSON response
```

不應該是：先查 User，再 foreach 每個 order 查 items，再 foreach 每個 item 查其他資料。

## 4. SQL Server 背後大概做什麼

### 重要 index 決策

| 查詢 | Index 理由 |
| --- | --- |
| user 依 email 查詢 | `UX_Users_Email` enforce uniqueness 並支援 seek |
| user 的 orders 依日期列出 | `(UserId, CreatedAt DESC, Id DESC)` 以 UserId 為 leading key，支援 keyset / order |
| order 的 items | `OrderItems(OrderId)` 支援 FK lookup 與 nested read |
| order list response | INCLUDE `Status`, `TotalAmount` 可減少 lookup，是否 covering 要看 projection |

### POST transaction

Order 建立通常要：

```text
驗證 user 存在且 active
    ↓
建立 Order
    ↓
建立多筆 OrderItems
    ↓
計算 / 驗證 TotalAmount
    ↓
SaveChanges
    ↓
Commit
```

如果另有扣庫存、寫 audit、建立 payment intent 等資料庫工作，transaction boundary 要清楚；外部支付 API 不應無限期放在 DB transaction 裡。

## 5. 與 C# / EF Core 的關聯

### Entity model

```csharp
public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] Version { get; set; } = [];
    public ICollection<Order> Orders { get; set; } = [];
}

public sealed class Order
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Status { get; set; } = "Pending";
    public decimal TotalAmount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<OrderItem> Items { get; set; } = [];
}

public sealed class OrderItem
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
```

### DbContext mapping

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<User>(entity =>
    {
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => x.Email).IsUnique();
        entity.Property(x => x.Email)
            .HasMaxLength(320)
            .IsUnicode();
        entity.Property(x => x.DisplayName)
            .HasMaxLength(200)
            .IsUnicode();
        entity.Property(x => x.Version).IsRowVersion();
    });

    modelBuilder.Entity<Order>(entity =>
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Status)
            .HasMaxLength(20)
            .IsUnicode(false);
        entity.Property(x => x.TotalAmount).HasPrecision(19, 4);
        entity.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");
        entity.HasIndex(x => new { x.UserId, x.CreatedAt, x.Id })
            .IncludeProperties(x => new { x.Status, x.TotalAmount })
            .IsDescending(false, true, true);
        entity.HasOne(x => x.User)
            .WithMany(x => x.Orders)
            .HasForeignKey(x => x.UserId);
    });

    modelBuilder.Entity<OrderItem>(entity =>
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ProductCode)
            .HasMaxLength(50)
            .IsUnicode(false);
        entity.Property(x => x.UnitPrice).HasPrecision(19, 4);
        entity.HasIndex(x => x.OrderId);
    });
}
```

### GET users：read-only projection + keyset

```csharp
public sealed record UserListItem(
    Guid Id,
    string Email,
    string DisplayName,
    DateTimeOffset CreatedAt);

public async Task<IReadOnlyList<UserListItem>> GetUsersAsync(
    DateTimeOffset? beforeCreatedAt,
    Guid? beforeId,
    int pageSize,
    CancellationToken cancellationToken)
{
    IQueryable<User> query = db.Users
        .AsNoTracking()
        .Where(x => x.IsActive)
        .OrderByDescending(x => x.CreatedAt)
        .ThenByDescending(x => x.Id)
        .AsQueryable();

    if (beforeCreatedAt is not null && beforeId is not null)
    {
        var cursorCreatedAt = beforeCreatedAt.Value;
        var cursorId = beforeId.Value;

        query = query.Where(x =>
            x.CreatedAt < cursorCreatedAt ||
            (x.CreatedAt == cursorCreatedAt && x.Id < cursorId));
    }

    return await query
        .Take(Math.Clamp(pageSize, 1, 100))
        .Select(x => new UserListItem(
            x.Id, x.Email, x.DisplayName, x.CreatedAt))
        .ToListAsync(cancellationToken);
}
```

### GET user orders：避免 N+1

```csharp
public sealed record OrderItemDto(
    string ProductCode,
    int Quantity,
    decimal UnitPrice);

public sealed record OrderDto(
    long Id,
    string Status,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderItemDto> Items);

public async Task<IReadOnlyList<OrderDto>> GetOrdersAsync(
    Guid userId,
    DateTimeOffset? beforeCreatedAt,
    long? beforeId,
    int pageSize,
    CancellationToken cancellationToken)
{
    IQueryable<Order> query = db.Orders
        .AsNoTracking()
        .Where(x => x.UserId == userId)
        .AsQueryable();

    if (beforeCreatedAt is not null && beforeId is not null)
    {
        var cursorCreatedAt = beforeCreatedAt.Value;
        var cursorId = beforeId.Value;
        query = query.Where(order =>
            order.CreatedAt < cursorCreatedAt
            || (order.CreatedAt == cursorCreatedAt && order.Id < cursorId));
    }

    return await query
        .OrderByDescending(x => x.CreatedAt)
        .ThenByDescending(x => x.Id)
        .Take(Math.Clamp(pageSize, 1, 100))
        .Select(x => new OrderDto(
            x.Id,
            x.Status,
            x.TotalAmount,
            x.CreatedAt,
            x.Items
                .OrderBy(i => i.Id)
                .Select(i => new OrderItemDto(
                    i.ProductCode,
                    i.Quantity,
                    i.UnitPrice))
                .ToList()))
        .ToListAsync(cancellationToken);
}
```

這個 query 固定 page size 上限 100，並用 `CreatedAt + Id` 建立完整排序。若要回傳下一頁，response 另帶最後一筆的兩個 cursor 值；不要把 `pageSize` 無上限交給 caller。使用前仍要檢查產生的 SQL、實際 row 數與 query plan。

### POST order：transaction + async

```csharp
public sealed record CreateOrderItemRequest(
    string ProductCode,
    int Quantity,
    decimal UnitPrice);

public sealed record CreateOrderRequest(
    Guid UserId,
    IReadOnlyList<CreateOrderItemRequest> Items);

public async Task<OrderDto> CreateOrderAsync(
    CreateOrderRequest request,
    CancellationToken cancellationToken)
{
    if (request.Items.Count == 0)
        throw new ArgumentException("Order needs items.");

    if (request.Items.Any(x => x.Quantity <= 0 || x.UnitPrice < 0))
        throw new ArgumentException("Invalid order item.");

    await using var transaction = await db.Database
        .BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);

    var userExists = await db.Users
        .AsNoTracking()
        .AnyAsync(x => x.Id == request.UserId && x.IsActive,
            cancellationToken);

    if (!userExists)
        throw new KeyNotFoundException("Active user not found.");

    var items = request.Items.Select(x => new OrderItem
    {
        ProductCode = x.ProductCode,
        Quantity = x.Quantity,
        UnitPrice = x.UnitPrice
    }).ToList();

    var order = new Order
    {
        UserId = request.UserId,
        Status = "Pending",
        CreatedAt = DateTimeOffset.UtcNow,
        TotalAmount = items.Sum(x => x.Quantity * x.UnitPrice),
        Items = items
    };

    db.Orders.Add(order);
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);

    return new OrderDto(
        order.Id,
        order.Status,
        order.TotalAmount,
        order.CreatedAt,
        order.Items
            .Select(x => new OrderItemDto(
                x.ProductCode, x.Quantity, x.UnitPrice))
            .ToList());
}
```

此範例的價格由 request 傳入只是教學簡化；真實電商通常從 server-side product / price source 取價，避免 client 任意改價，並把 inventory / payment 的 transaction boundary 分開設計。

這裡用 `Serializable` 保護「確認 active user → 建立 order」的 invariant，但會增加 blocking／deadlock 成本；也可以用 `UPDLOCK` 鎖住該 user row，並配合固定鎖定順序與有限 retry。若只使用預設 `READ COMMITTED`，active check 與後續寫入之間仍可能被其他 transaction 改變。

### API error contract

| 情況 | HTTP status | 回應 |
| --- | ---: | --- |
| JSON／model validation 失敗 | 400 | `ProblemDetails` 或 validation details |
| user／order 不存在 | 404 | `ProblemDetails` |
| rowversion／domain concurrency conflict | 409 | `ProblemDetails`，告知重新讀取後再試 |
| dependency 暫時失敗 | 503 | `ProblemDetails`，附 trace metadata |
| 未預期例外 | 500 | 不洩漏內部 exception |

`IExceptionHandler` 應集中把 domain exception 映射到上述 contract；不要讓 service 的 `ArgumentException` 未處理地變成 500，也不要在 controller 各自組不同 error JSON。

## 6. 常見誤區

- `Include` 能用不表示要用；API read model 多半 projection 更精準。
- `AsNoTracking` 適合 read-only，不適合直接修改後期待 `SaveChangesAsync` 偵測。
- `Take` 沒有穩定 `OrderBy`，pagination 可能重複或漏資料。
- order total 不能信任 client；範例只為展示 query shape。
- 單次 `SaveChangesAsync` 可能已包 transaction，但跨多段讀寫 / side effect 時仍需重新檢視 boundary。
- Index 設計要從 endpoint query shape 與實際 workload 驗證，不是看到欄位就加 index。

## 7. 面試回答

> User / Order API 的 read endpoint 我會以 `AsNoTracking` + projection 直接產生 DTO，避免載入不必要 entity graph 與 N+1；orders 會用 `UserId, CreatedAt, Id` 的 composite index 支援查詢與 keyset pagination。建立 order 時驗證 active user、建立 order 與 items，必要時包在短 transaction 內，`SaveChangesAsync` 傳 cancellation token。若資料量增大，優先檢查 generated SQL、index、pagination、tracking、cartesian explosion 與 transaction duration。

## 8. 小練習

1. 為 `GET /api/users/{id}/orders` 找出至少一個 index。
2. 把 orders endpoint 改成 keyset pagination。
3. 為 Order 加上 `rowversion`，設計修改 Order Status 的 optimistic concurrency handling。

## 從 1 萬筆到 1,000 萬筆的檢查表

1. 是否仍 `SELECT *` 或載入完整 entity graph？
2. 是否有 stable ordering、composite index 與 keyset pagination？
3. 是否出現 deep OFFSET、N+1、Key Lookup、Sort spill？
4. 是否把 read-only query 改成 `AsNoTracking` / projection？
5. 是否有過長 transaction、blocking 或 deadlock？
6. 是否需要 partitioning、archival、summary table 或 read model？
7. EF generated SQL 與 production-like actual plan 是否驗證過？
