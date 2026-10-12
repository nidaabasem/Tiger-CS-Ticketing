/*
  READ-ONLY probe (writes only to #temp tables): run on the Ticketing server as the application login BEFORE enabling the refresh job.
  Confirms, per company, which result-set shape the DEPLOYED procedures return and what the data looks like.
  Each EXEC runs the full PACT ledger work (the earlier measurement for company 4 was ~58 s), so expect a few minutes.
  Company 32 has not been tested through the linked server yet - this is that test.
  Paste the printed output and the final result sets back into the review; do not assume the older definitions match.
*/
SET NOCOUNT ON;
SET REMOTE_PROC_TRANSACTIONS OFF;
USE [TigerCsTicketing];

DECLARE @Start datetime = '20260101', @End datetime = '20261031 23:59:59.997';   -- a narrow window keeps the probe small

DECLARE @company int = 4;
WHILE @company IN (4, 32)
BEGIN
    DECLARE @proc nvarchar(400) = CONCAT(N'[10.10.10.94].[PACTRPT].[dbo].', QUOTENAME(CONCAT(N'p', @company, N'AccountReceivables')));
    PRINT CONCAT(N'===== company ', @company, N' : ', @proc, N' =====');
    IF OBJECT_ID(N'tempdb..#probe') IS NOT NULL DROP TABLE #probe;
    CREATE TABLE #probe (CompanyID int NULL, ProjectCode nvarchar(200) NULL, UnitCode nvarchar(200) NULL, TenantID nvarchar(100) NULL,
        FullName nvarchar(400) NULL, Mobile nvarchar(100) NULL, Email nvarchar(400) NULL, UnitID bigint NULL, VoucherNumber nvarchar(100) NULL,
        ChequeNumber nvarchar(100) NULL, DueDate datetime NULL, Amount decimal(19,4) NULL, Status nvarchar(50) NULL);

    DECLARE @variant tinyint = 1, @ok bit = 0, @sql nvarchar(max), @cols nvarchar(max);
    WHILE @variant <= 4 AND @ok = 0
    BEGIN
        SET @cols = CASE @variant
            WHEN 1 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
            WHEN 2 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount'
            WHEN 3 THEN N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
            ELSE N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount' END;
        SET @sql = CONCAT(N'INSERT INTO #probe (', @cols, N') EXEC ', @proc, N' @StartDate = @s, @EndDate = @e, @MinAmount = 0;');
        BEGIN TRY
            EXEC sys.sp_executesql @sql, N'@s datetime, @e datetime', @s = @Start, @e = @End;
            SET @ok = 1;
            PRINT CONCAT(N'Shape variant ', @variant, N' matches: ', @cols);
        END TRY
        BEGIN CATCH
            PRINT CONCAT(N'Variant ', @variant, N' failed: error ', ERROR_NUMBER(), N' - ', LEFT(ERROR_MESSAGE(), 300));
            DELETE FROM #probe;
            SET @variant += 1;
        END CATCH;
    END;

    IF @ok = 1
    BEGIN
        SELECT @company AS Company, @variant AS ShapeVariant, COUNT(*) AS Rows_, SUM(CASE WHEN Amount = 0 THEN 1 ELSE 0 END) AS ZeroRows,
               SUM(CASE WHEN Amount < 0 THEN 1 ELSE 0 END) AS NegativeRows, SUM(CASE WHEN Amount > 0 AND Amount < 1 THEN 1 ELSE 0 END) AS SubDirhamRows,
               SUM(CASE WHEN UnitID IS NULL OR UnitID <= 0 THEN 1 ELSE 0 END) AS InvalidUnitRows,
               SUM(CASE WHEN NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NULL THEN 1 ELSE 0 END) AS BlankTenantRows,
               SUM(CASE WHEN CompanyID <> @company OR CompanyID IS NULL THEN 1 ELSE 0 END) AS WrongCompanyRows,
               SUM(CASE WHEN ProjectCode IS NULL OR ProjectCode = N'' THEN 1 ELSE 0 END) AS BlankProjectCodeRows,
               MIN(DueDate) AS MinDue, MAX(DueDate) AS MaxDue, MIN(Amount) AS MinAmount, MAX(Amount) AS MaxAmount
          FROM #probe;
        SELECT Status, COUNT(*) AS Rows_, MIN(Amount) AS MinAmount, MAX(Amount) AS MaxAmount FROM #probe GROUP BY Status;      -- expect Paid(=0) / Installment(>0)
        ;WITH t AS (SELECT dbo.fn_CollectionsTowerNumber(UnitCode) AS TowerNumber, Amount FROM #probe)
        SELECT @company AS Company, t.TowerNumber, COUNT(*) AS Rows_, SUM(CASE WHEN t.Amount > 0 THEN t.Amount ELSE 0 END) AS RemainingAmount,
               CASE WHEN t.TowerNumber IS NULL THEN 'NO TOWER NUMBER'
                    WHEN EXISTS (SELECT 1 FROM dbo.CollectionsTowers x WHERE x.CompanyId = @company
                                 AND LTRIM(RTRIM(CONVERT(nvarchar(20), x.TowerNumber))) = t.TowerNumber) THEN 'Matched' ELSE 'UNMATCHED' END AS TowerMatch
          FROM t GROUP BY t.TowerNumber ORDER BY TowerMatch DESC, t.TowerNumber;
        SELECT TOP (10) * FROM #probe WHERE Amount > 0 ORDER BY DueDate;
    END;
    SET @company = CASE @company WHEN 4 THEN 32 ELSE 0 END;
END;
