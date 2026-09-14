USE [Orders];
GO

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
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
    CREATE UNIQUE INDEX UX_Users_Email ON dbo.Users(Email);
END;
GO

IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL
BEGIN
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
END;
GO
