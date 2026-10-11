/*
  Snapshot fetch path - targeted test (run on 10.10.10.117, database TigerCsTicketing). RUN ONE SECTION AT A TIME (select it, F5).

  Purpose: compare the direct call that works
        EXEC [10.10.10.94].[PACTRPT].[dbo].[p32AccountReceivables] @StartDate='20260901', @EndDate='20261011', @MinAmount=100;
  with what dbo.usp_Collections_RefreshReceivables does for the same company and the SAME window, changing ONE thing per section:
        section 1  control: the direct call (a result grid; read its COLUMN HEADERS, see section 2)
        section 3  the refresh statement: INSERT ... EXEC into a temp copy of the staging table, typed parameters (datetime / int) through
                   sp_executesql, SET REMOTE_PROC_TRANSACTIONS OFF, no outer transaction  -> shows 7391 / 213 / conversion errors, row counts, timing
        section 4  same with @MinAmount = 0 (the configured SourceMinAmount)
        section 5  (only if section 3 failed with 7391) the same statement with REMOTE_PROC_TRANSACTIONS ON, to separate "distributed transaction" from "layout"
        section 6  (heavy, optional) the real refresh window 2000-01-01 .. 2099-12-31, timed
  The window 2026-09-01 .. 2026-10-11 is DIAGNOSTIC ONLY. Nothing here changes the application's window, SourceMinAmount, companies or any persistent table:
  it writes only to #stage (a temp table) and reads from the linked server. No linked-server / MSDTC setting is changed.

  Why there is NO TRY/CATCH around the INSERT ... EXEC: on SQL Server 2022 (Linux, CU27), a column-layout mismatch of an INSERT ... EXEC through a linked
  server INSIDE a TRY block terminated the whole session without a CATCH (it is returned as a normal error 213 when not inside TRY). Run the sections
  separately so a wrong layout shows as a plain error message instead.
*/

-- ============================== section 0: settings (re-run this line in every new window) ==============================
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName, s.name AS LinkedServer, s.provider, s.is_data_access_enabled AS DataAccess, s.is_rpc_out_enabled AS RpcOut,
       s.is_remote_proc_transaction_promotion_enabled AS RemoteProcTransactionPromotion, s.query_timeout AS LinkedQueryTimeoutS,
       (SELECT value_in_use FROM sys.configurations WHERE name = N'remote query timeout (s)') AS ServerRemoteQueryTimeoutS, @@TRANCOUNT AS OpenTransactions
  FROM sys.servers s WHERE s.name = N'10.10.10.94';
-- the refresh remembers the layout that worked: NULL = it will START with layout 1 (13 columns incl. ProjectCode and Status)
SELECT CompanyId, ShapeVariant AS RememberedLayout, LastAttemptStatus, LastErrorNumber FROM dbo.CollectionsReceivableCompanyState ORDER BY CompanyId;
GO

-- ============================== section 1: control (the call that works) ==============================
SET REMOTE_PROC_TRANSACTIONS OFF;
EXEC [10.10.10.94].[PACTRPT].[dbo].[p32AccountReceivables] @StartDate = '20260901', @EndDate = '20261011', @MinAmount = 100;
GO

/*
  ============================== section 2: choose the layout from the HEADERS of section 1 (positional INSERT ... EXEC) ==============================
    1  CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status      (13 columns)
    2  CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount              (12)
    3  CompanyID,              UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status      (12)
    4  CompanyID,              UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount              (11)
  Same ORDER and NAMES as the headers, nothing more, nothing less. Edit @Layout below in sections 3-6 to that number.
*/

-- ============================== section 3: the refresh statement, same window, MinAmount 100 ==============================
IF OBJECT_ID('tempdb..#stage') IS NOT NULL DROP TABLE #stage;
SELECT TOP (0) CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status INTO #stage FROM dbo.CollectionsReceivableStaging;
SET REMOTE_PROC_TRANSACTIONS OFF;
DECLARE @Layout tinyint = 3;                        -- <== EDIT from section 2
DECLARE @CompanyId int = 32, @From date = '20260901', @Through date = '20261011', @MinAmount int = 100;
DECLARE @startDt datetime = CAST(@From AS datetime), @endDt datetime = DATEADD(MILLISECOND, -3, DATEADD(DAY, 1, CAST(@Through AS datetime)));   -- 23:59:59.997, as the refresh
DECLARE @cols nvarchar(max) = CASE @Layout
    WHEN 1 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
    WHEN 2 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount'
    WHEN 3 THEN N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
    ELSE        N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount' END;
DECLARE @sql nvarchar(max) = CONCAT(N'INSERT INTO #stage (', @cols, N') EXEC ', QUOTENAME(N'10.10.10.94'), N'.', QUOTENAME(N'PACTRPT'), N'.', QUOTENAME(N'dbo'), N'.',
                                    QUOTENAME(CONCAT(N'p', @CompanyId, N'AccountReceivables')), N' @StartDate = @StartDate, @EndDate = @EndDate, @MinAmount = @MinAmount;');
DECLARE @t0 datetime2(3) = SYSUTCDATETIME();
EXEC sys.sp_executesql @sql, N'@StartDate datetime, @EndDate datetime, @MinAmount int', @StartDate = @startDt, @EndDate = @endDt, @MinAmount = @MinAmount;
SELECT 'section 3: INSERT-EXEC succeeded' AS Result, @Layout AS Layout, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()) AS ElapsedMs, @@TRANCOUNT AS OpenTransactions;
SELECT COUNT(*) AS RawRows, SUM(CASE WHEN CompanyID = @CompanyId THEN 1 ELSE 0 END) AS RowsOfThisCompany, SUM(CASE WHEN CompanyID <> @CompanyId OR CompanyID IS NULL THEN 1 ELSE 0 END) AS RowsOfAnyOtherCompany,
       SUM(CASE WHEN Amount > 0 THEN 1 ELSE 0 END) AS PositiveAmount, SUM(CASE WHEN UnitID > 0 THEN 1 ELSE 0 END) AS UnitIdPositive,
       SUM(CASE WHEN ISNULL(Mobile, N'') = N'' THEN 1 ELSE 0 END) AS EmptyMobile, SUM(CASE WHEN ISNULL(Email, N'') = N'' THEN 1 ELSE 0 END) AS EmptyEmail,
       SUM(CASE WHEN ISNULL(Mobile, N'') = N'' AND ISNULL(Email, N'') = N'' THEN 1 ELSE 0 END) AS NoContactAtAll, MIN(DueDate) AS MinDue, MAX(DueDate) AS MaxDue
  FROM #stage;
SELECT TOP (20) CompanyID, UnitCode, TenantID, Mobile, Email, UnitID, VoucherNumber, DueDate, Amount, Status FROM #stage WHERE UnitCode IN (N'TP140-1807', N'TP140-3207') ORDER BY UnitCode, DueDate;
-- If this section fails: 7391 = the distributed-transaction problem of INSERT ... EXEC (go to section 5); 213 or 245/8114 = wrong @Layout (compare section 1 headers);
-- anything else: send the number and message.
GO

-- ============================== section 4: same, MinAmount = 0 (the configured SourceMinAmount) ==============================
-- Repeat section 3 with: DECLARE ... @MinAmount int = 0;   (only that value changes). The row count must be >= the MinAmount 100 count; paid rows (Amount = 0) appear here.
GO

-- ============================== section 5: only if section 3 failed with 7391 ==============================
-- Repeat section 3 with the first line after the temp-table creation changed to:  SET REMOTE_PROC_TRANSACTIONS ON;
-- Different outcome with ON vs OFF => the transaction context is the difference. Same 7391 both ways => the linked server's promotion / MSDTC setting is what must be
-- decided by the DBA (this script and the application change neither).
GO

-- ============================== section 6: heavy, optional - the real refresh window ==============================
-- Repeat section 3 with @From = '20000101', @Through = '20991231', @MinAmount = 0. The command timeout of the refresh is 1800 s, the server's remote query
-- timeout is shown in section 0 (0 = unlimited, default 600 s). Record ElapsedMs and RawRows; if it exceeds the remote query timeout the failure is a timeout, not a layout.
GO

/*
  ============================== section 7: pin the layout that worked (only after section 3 succeeded) ==============================
  The refresh tries the remembered layout FIRST. For Sharjah the remembered layout is empty, so it starts with layout 1; if p32 does not return ProjectCode,
  every first attempt is a mismatch. Run tools/set_receivables_shape_variant.sql with @CompanyId = 32 and @ShapeVariant = <the @Layout of section 3>.
*/
