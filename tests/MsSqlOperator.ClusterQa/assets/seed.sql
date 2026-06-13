SET NOCOUNT ON;
IF DB_ID('ShopDB') IS NULL CREATE DATABASE ShopDB;
GO
ALTER DATABASE ShopDB SET RECOVERY FULL;
GO
USE ShopDB;
GO
IF OBJECT_ID('dbo.Customers') IS NULL
CREATE TABLE dbo.Customers (
    CustomerId INT IDENTITY PRIMARY KEY,
    Name       NVARCHAR(100) NOT NULL,
    Email      NVARCHAR(200) NOT NULL,
    CreatedAt  DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.Products') IS NULL
CREATE TABLE dbo.Products (
    ProductId  INT IDENTITY PRIMARY KEY,
    Sku        NVARCHAR(40) NOT NULL,
    Name       NVARCHAR(120) NOT NULL,
    PriceCents INT NOT NULL);
IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders (
    OrderId    INT IDENTITY PRIMARY KEY,
    CustomerId INT NOT NULL,
    PlacedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    TotalCents INT NOT NULL);
-- Continuous-writer probe table: records which replica served each committed write,
-- so the failover handoff (and any interruption gap) is reconstructable from the data.
IF OBJECT_ID('dbo.HaProbe') IS NULL
CREATE TABLE dbo.HaProbe (
    Seq    INT PRIMARY KEY,
    Ts     DATETIME2 NOT NULL,
    Writer NVARCHAR(128) NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM dbo.Customers)
BEGIN
    ;WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
    INSERT dbo.Customers(Name, Email)
    SELECT CONCAT('Customer ', i), CONCAT('cust', i, '@example.com') FROM n;

    ;WITH n AS (SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) i FROM sys.all_objects)
    INSERT dbo.Products(Sku, Name, PriceCents)
    SELECT CONCAT('SKU-', RIGHT('00000'+CAST(i AS VARCHAR),5)), CONCAT('Product ', i), 100+(i*7)%9900 FROM n;

    ;WITH n AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
    INSERT dbo.Orders(CustomerId, TotalCents)
    SELECT 1+ABS(CHECKSUM(NEWID()))%2000, 100+ABS(CHECKSUM(NEWID()))%50000 FROM n;
END
GO
SELECT 'Customers' tbl, COUNT(*) cnt FROM dbo.Customers
UNION ALL SELECT 'Products', COUNT(*) FROM dbo.Products
UNION ALL SELECT 'Orders', COUNT(*) FROM dbo.Orders;
GO
