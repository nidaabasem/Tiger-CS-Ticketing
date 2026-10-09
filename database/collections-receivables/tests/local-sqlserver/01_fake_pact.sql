-- Synthetic PACT data (NOT real receivables): FakeConfig controls volume and an artificial delay; dbo.p4/p32AccountReceivables mimic the DEPLOYED output shape,
-- dbo.p4/p32AccountReceivablesV2 mimic the companion shape (adds PlanAmount / AllocatedAmount). Never run against a real PACT server.
USE PACTRPT;
GO
IF OBJECT_ID('dbo.FakeConfig') IS NULL CREATE TABLE dbo.FakeConfig (CompanyId int PRIMARY KEY, Tenants int NOT NULL, Instalments int NOT NULL, DelaySeconds int NOT NULL, Shape int NOT NULL DEFAULT 1);
MERGE dbo.FakeConfig t USING (VALUES (4,25000,12,0,1),(32,12000,12,0,1)) s(CompanyId,Tenants,Instalments,DelaySeconds,Shape) ON t.CompanyId=s.CompanyId
WHEN MATCHED THEN UPDATE SET Tenants=s.Tenants, Instalments=s.Instalments, DelaySeconds=s.DelaySeconds, Shape=s.Shape
WHEN NOT MATCHED THEN INSERT VALUES (s.CompanyId,s.Tenants,s.Instalments,s.DelaySeconds,s.Shape);
GO
-- Hand-written edge-case rows (03_edge_cases.sql) that are appended to the generated rows of the deployed shape: ambiguous same-day instalments,
-- contradictory status, conflicting contact details, invalid phones / e-mails, threshold boundaries... Empty unless that script is run.
IF OBJECT_ID('dbo.FakeEdgeRows') IS NULL
CREATE TABLE dbo.FakeEdgeRows (CompanyId int NOT NULL, ProjectCode varchar(20) NOT NULL, UnitCode varchar(60) NOT NULL, TenantID varchar(20) NOT NULL, FullName nvarchar(100) NOT NULL,
    Mobile varchar(30) NOT NULL, Email varchar(100) NOT NULL, UnitID int NOT NULL, VoucherNumber varchar(40) NOT NULL, ChequeNumber varchar(20) NOT NULL DEFAULT '',
    DueDate datetime NOT NULL, Amount float NOT NULL, Status varchar(20) NOT NULL);
GO
-- Deterministic synthetic receivables shaped like the deployed procedures (variant 1 column order). NOT PACT data.
CREATE OR ALTER PROCEDURE dbo.FakeReceivables @Company int, @StartDate datetime, @EndDate datetime, @MinAmount int
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @T int, @I int, @D int, @Shape int;
  SELECT @T = Tenants, @I = Instalments, @D = DelaySeconds, @Shape = Shape FROM dbo.FakeConfig WHERE CompanyId = @Company;
  IF @D > 0 BEGIN DECLARE @w char(8) = CONVERT(char(8), DATEADD(SECOND, @D, 0), 108); WAITFOR DELAY @w; END;
  CREATE TABLE #n (n int PRIMARY KEY);
  INSERT #n SELECT TOP (@T) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
  CREATE TABLE #k (k int PRIMARY KEY);
  INSERT #k SELECT TOP (@I) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects;
  SELECT @Company AS CompanyID,
         CAST('' AS varchar(20)) AS ProjectCode,
         CAST('TP' + CAST(tw.tower AS varchar(10)) + '-' + CAST(1000 + (n.n % 900) AS varchar(10)) + CASE WHEN n.n % 7 = 0 THEN '-A' ELSE '' END AS varchar(60)) AS UnitCode,
         CAST(500000 + n.n AS varchar(20)) AS TenantID,
         CAST('Customer ' + CAST(n.n AS varchar(10)) AS nvarchar(100)) AS FullName,
         CAST('+97150' + RIGHT('0000000' + CAST(n.n AS varchar(10)), 7) AS varchar(30)) AS Mobile,
         CAST('c' + CAST(n.n AS varchar(10)) + '@example.test' AS varchar(100)) AS Email,
         CASE WHEN (n.n * 31 + k.k) % 200 = 0 THEN 0 ELSE CAST(n.n * 10 + 1 AS int) END AS UnitID,
         CAST('INV-' + CAST(n.n AS varchar(10)) + '-' + CAST(k.k AS varchar(5)) AS varchar(40)) AS VoucherNumber,
         CAST('' AS varchar(20)) AS ChequeNumber,
         DATEADD(MONTH, k.k - 1, CAST('20251115' AS datetime)) AS DueDate,
         CAST(CASE WHEN k.k <= (n.n % @I) + 1 THEN 0               -- paid (older instalments)
                   WHEN (n.n + k.k) % 20 = 0 THEN 40.5              -- sub-100 balance
                   ELSE 300 + ((n.n * 17 + k.k * 13) % 9000) END AS float) AS Amount,
         CAST(CASE WHEN k.k <= (n.n % @I) + 1 THEN 'Paid' ELSE 'Installment' END AS varchar(20)) AS Status
    INTO #res
    FROM #n n CROSS JOIN #k k
    CROSS APPLY (SELECT CASE WHEN @Company = 32 THEN CHOOSE(n.n % 4 + 1, 127, 140, 127, 140) ELSE 101 + (n.n % 30) + CASE WHEN n.n % 400 = 0 THEN 18 ELSE 0 END END AS tower) tw;
  INSERT #res (CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status)
    SELECT CompanyId, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status
      FROM dbo.FakeEdgeRows WHERE CompanyId = @Company;
  IF @Shape = 1
    SELECT CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status
      FROM #res WHERE DueDate BETWEEN @StartDate AND @EndDate AND Amount >= @MinAmount ORDER BY DueDate DESC;
  ELSE
    SELECT CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount
      FROM #res WHERE DueDate BETWEEN @StartDate AND @EndDate AND Amount >= @MinAmount ORDER BY DueDate DESC;
END
GO
CREATE OR ALTER PROCEDURE dbo.p4AccountReceivables @StartDate datetime, @EndDate datetime, @MinAmount int AS EXEC dbo.FakeReceivables 4, @StartDate, @EndDate, @MinAmount;
GO
CREATE OR ALTER PROCEDURE dbo.p32AccountReceivables @StartDate datetime, @EndDate datetime, @MinAmount int AS EXEC dbo.FakeReceivables 32, @StartDate, @EndDate, @MinAmount;
GO
-- Companion (V2-shaped) fake: same synthetic data, plus PaymentTermAccountId / PlanAmount / AllocatedAmount (Amount = Plan - Allocated).
CREATE OR ALTER PROCEDURE dbo.FakeReceivablesV2 @Company int, @StartDate datetime, @EndDate datetime, @MinAmount decimal(19,4), @IncludeSettled bit, @StrictIdentity bit
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @T int, @I int;
  SELECT @T = Tenants, @I = Instalments FROM dbo.FakeConfig WHERE CompanyId = @Company;
  CREATE TABLE #n (n int PRIMARY KEY); INSERT #n SELECT TOP (@T) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
  CREATE TABLE #k (k int PRIMARY KEY); INSERT #k SELECT TOP (@I) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects;
  SELECT x.*, CAST(x.Plan_ - x.Alloc_ AS decimal(19,4)) AS Remaining INTO #r FROM (
    SELECT n.n, k.k, tw.tower,
           CAST(300 + ((n.n * 17 + k.k * 13) % 9000) AS decimal(19,4)) AS Plan_,
           CAST(CASE WHEN k.k <= (n.n % @I) + 1 THEN 300 + ((n.n * 17 + k.k * 13) % 9000)           -- fully paid
                     WHEN (n.n + k.k) % 5 = 0 THEN ROUND((300 + ((n.n * 17 + k.k * 13) % 9000)) * 0.4, 2)  -- partially paid
                     ELSE 0 END AS decimal(19,4)) AS Alloc_
      FROM #n n CROSS JOIN #k k
      CROSS APPLY (SELECT CASE WHEN @Company = 32 THEN CHOOSE(n.n % 4 + 1, 127, 140, 127, 140) ELSE 101 + (n.n % 30) + CASE WHEN n.n % 400 = 0 THEN 18 ELSE 0 END END AS tower) tw) x;
  SELECT @Company AS CompanyID, CAST('' AS varchar(20)) AS ProjectCode,
         CAST('TP' + CAST(r.tower AS varchar(10)) + '-' + CAST(1000 + (r.n % 900) AS varchar(10)) + CASE WHEN r.n % 7 = 0 THEN '-A' ELSE '' END AS varchar(60)) AS UnitCode,
         CAST(500000 + r.n AS varchar(20)) AS TenantID, CAST('Customer ' + CAST(r.n AS varchar(10)) AS nvarchar(100)) AS FullName,
         CAST('+97150' + RIGHT('0000000' + CAST(r.n AS varchar(10)), 7) AS varchar(30)) AS Mobile, CAST('c' + CAST(r.n AS varchar(10)) + '@example.test' AS varchar(100)) AS Email,
         CASE WHEN (r.n * 31 + r.k) % 200 = 0 THEN 0 ELSE CAST(r.n * 10 + 1 AS int) END AS UnitID,
         CAST('INV-' + CAST(r.n AS varchar(10)) + '-' + CAST(r.k AS varchar(5)) AS varchar(40)) AS VoucherNumber, CAST('' AS varchar(20)) AS ChequeNumber,
         DATEADD(MONTH, r.k - 1, CAST('20251115' AS datetime)) AS DueDate,
         r.Remaining AS Amount, CAST(CASE WHEN r.Remaining = 0 THEN 'Paid' ELSE 'Installment' END AS varchar(20)) AS Status,
         CAST(r.n * 100 + r.k AS bigint) AS PaymentTermAccountId, r.Plan_ AS PlanAmount, r.Alloc_ AS AllocatedAmount
    FROM #r r
   WHERE DATEADD(MONTH, r.k - 1, CAST('20251115' AS datetime)) BETWEEN @StartDate AND @EndDate AND r.Remaining >= @MinAmount AND (@IncludeSettled = 1 OR r.Remaining > 0)
   ORDER BY 12 DESC;
END
GO
CREATE OR ALTER PROCEDURE dbo.p4AccountReceivablesV2 @StartDate datetime, @EndDate datetime, @MinAmount decimal(19,4) = 0, @IncludeSettled bit = 0, @StrictIdentity bit = 1 AS EXEC dbo.FakeReceivablesV2 4, @StartDate, @EndDate, @MinAmount, @IncludeSettled, @StrictIdentity;
GO
CREATE OR ALTER PROCEDURE dbo.p32AccountReceivablesV2 @StartDate datetime, @EndDate datetime, @MinAmount decimal(19,4) = 0, @IncludeSettled bit = 0, @StrictIdentity bit = 1 AS EXEC dbo.FakeReceivablesV2 32, @StartDate, @EndDate, @MinAmount, @IncludeSettled, @StrictIdentity;
GO
