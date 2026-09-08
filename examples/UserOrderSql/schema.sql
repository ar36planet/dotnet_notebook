/*
   教材用 User / Order / OrderItem schema。
   這份 script 假設在一個可修改的 learning database 執行；不要直接對 production 執行。
*/

IF OBJECT_ID(N'dbo.OrderItems', N'U') IS NOT NULL DROP TABLE dbo.OrderItems;
IF OBJECT_ID(N'dbo.Orders', N'U') IS NOT NULL DROP TABLE dbo.Orders;
IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL DROP TABLE dbo.Users;
GO

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
GO

CREATE UNIQUE INDEX UX_Users_Email
ON dbo.Users(Email);
GO

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
GO

CREATE INDEX IX_Orders_UserId_CreatedAt
ON dbo.Orders(UserId, CreatedAt DESC, Id DESC)
INCLUDE (Status, TotalAmount);
GO

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
GO

CREATE INDEX IX_OrderItems_OrderId
ON dbo.OrderItems(OrderId)
INCLUDE (ProductCode, Quantity, UnitPrice);
GO
