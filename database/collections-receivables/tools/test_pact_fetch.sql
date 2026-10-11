/*
  Targeted test of the PACT fetch for ONE company (TigerCsTicketing).  Run section by section.  Changes nothing persistent: it fetches into a TEMP table, exactly the way
  dbo.usp_CollectionsRcv_Refresh does (INSERT ... EXEC, typed parameters, SET REMOTE_PROC_TRANSACTIONS OFF, the company's verified result layout) and reports rows, time and
  whether every row belongs to the company.  It does not touch dbo.CollectionsRcv*, the linked server, MSDTC or any server setting.

  Layouts (verified on the live procedures): company 4 -> layout 1 (13 columns, ProjectCode), company 32 -> layout 3 (12 columns, NO ProjectCode).
  WARNING: INSERT ... EXEC maps by position and a layout that does not match can END THE SESSION without an error message (SQL Server 2022 through a linked server). Do not
  change @Layout unless you have just looked at the procedure's result columns (section 1 shows them).
  Set @CompanyId, then @From / @Through: the diagnostic window (default: a few weeks) or the production window 2000-01-01 .. 2099-12-31 (the full-window run: record the time and rows).
*/
SET NOCOUNT ON;
SET REMOTE_PROC_TRANSACTIONS OFF;
DECLARE @CompanyId int = 32;
DECLARE @From date = '20260901', @Through date = '20261011', @MinAmount int = 0;
DECLARE @Layout tinyint = CASE WHEN @CompanyId = 32 THEN 3 ELSE 1 END;

-- 1. The result columns the procedure really returns (compare with the layout).  Skipped quietly if the remote server does not allow the describe call.
BEGIN TRY
    DECLARE @describe nvarchar(max) = CONCAT(N'EXEC [10.10.10.94].[PACTRPT].sys.sp_describe_first_result_set @tsql = N''EXEC dbo.p', @CompanyId, N'AccountReceivables @StartDate = ''''', CONVERT(char(8), @From, 112), N''''', @EndDate = ''''', CONVERT(char(8), @Through, 112), N''''', @MinAmount = 0''');
    EXEC (@describe);
END TRY
BEGIN CATCH
    PRINT N'Section 1 skipped (describe not available through the linked server): ' + ERROR_MESSAGE();
END CATCH;

-- 2. The fetch itself.
CREATE TABLE #src (CompanyID int NULL, ProjectCode nvarchar(200) NULL, UnitCode nvarchar(200) NULL, TenantID nvarchar(100) NULL, FullName nvarchar(400) NULL, Mobile nvarchar(100) NULL,
                   Email nvarchar(400) NULL, UnitID bigint NULL, VoucherNumber nvarchar(100) NULL, ChequeNumber nvarchar(100) NULL, DueDate datetime NULL, Amount decimal(19,4) NULL, Status nvarchar(50) NULL);
DECLARE @cols nvarchar(max) = CASE @Layout WHEN 1 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
                                           ELSE N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status' END;
DECLARE @sql nvarchar(max) = CONCAT(N'INSERT INTO #src (', @cols, N') EXEC [10.10.10.94].[PACTRPT].[dbo].', QUOTENAME(CONCAT(N'p', @CompanyId, N'AccountReceivables')),
                                    N' @StartDate = @StartDate, @EndDate = @EndDate, @MinAmount = @MinAmount;');
DECLARE @t0 datetime2(3) = SYSUTCDATETIME(), @s datetime = CAST(@From AS datetime), @e datetime = DATEADD(MILLISECOND, -3, DATEADD(DAY, 1, CAST(@Through AS datetime)));
EXEC sys.sp_executesql @sql, N'@StartDate datetime, @EndDate datetime, @MinAmount int', @StartDate = @s, @EndDate = @e, @MinAmount = @MinAmount;
SELECT @CompanyId AS CompanyId, @Layout AS Layout, @From AS FromDate, @Through AS ThroughDate, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()) AS FetchMs,
       COUNT(*) AS RawRows, SUM(CASE WHEN Amount > 0 THEN 1 ELSE 0 END) AS UnpaidRows, SUM(CASE WHEN Amount > 0 THEN Amount ELSE 0 END) AS UnpaidTotal,
       SUM(CASE WHEN CompanyID IS NULL OR CompanyID <> @CompanyId THEN 1 ELSE 0 END) AS RowsOfAnotherCompany,
       SUM(CASE WHEN UnitCode LIKE N'%*%' THEN 1 ELSE 0 END) AS CancelledUnitRows, SUM(CASE WHEN UnitID IS NULL OR UnitID <= 0 THEN 1 ELSE 0 END) AS UnitIdZeroRows,
       SUM(CASE WHEN ISNULL(Mobile, N'') = N'' THEN 1 ELSE 0 END) AS WithoutMobile, SUM(CASE WHEN ISNULL(Email, N'') = N'' THEN 1 ELSE 0 END) AS WithoutEmail,
       MIN(DueDate) AS OldestDue, MAX(DueDate) AS NewestDue
  FROM #src;
SELECT TOP (5) * FROM #src WHERE Amount > 0 ORDER BY DueDate DESC;
DROP TABLE #src;
