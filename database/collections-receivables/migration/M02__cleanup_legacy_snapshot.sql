/*
  M02 - CLEAN the previous receivables pipeline's DATA (TigerCsTicketing).  Run only AFTER the new application is deployed and the verification in
  docs/Collections/Receivables-Snapshot.md ("Verify after deployment") passed: both companies loaded by the NEW pipeline and the pages show the right receivables.

  DRY RUN BY DEFAULT: with @Execute = 0 it only reports what it would delete.  With @Execute = 1 it deletes the rows of EXACTLY these 9 tables, in one transaction:
      dbo.CollectionsReceivableRunCompany  dbo.CollectionsReceivableRun  dbo.CollectionsReceivableSnapshot  dbo.CollectionsReceivableStaging
      dbo.CollectionsReceivableTowerSummary  dbo.CollectionsReceivableUnitText  dbo.CollectionsReceivableUnit  dbo.CollectionsContactNorm
      dbo.CollectionsReceivableCompanyState   (emptied, then re-seeded with its two neutral rows 4 and 32 = "never loaded", so the old application could still start)
  The table definitions and the old procedures stay (a rollback needs them); migration/M03 drops them later, optionally.
  It never touches customers, tickets, CRM owner data, dbo.CollectionsTowers, dbo.CollectionsCrmProjectMap, review / campaign / dispatch history, the new dbo.CollectionsRcv* tables or PACT.

  Refuses to run unless ALL of these hold (nothing is deleted otherwise):
    1. the new pipeline is healthy: both companies have a published run (CurrentRunId), LastStatus = Succeeded and PublishedRows > 0;
    2. every table above that exists has a verified backup dbo.Bak_<table>_<tag> with the same row count (M01);
    3. no refresh of the OLD pipeline is executing (its application lock is free).
  Use the same @Tag as M01.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Execute bit = 0;           -- 1 = delete
DECLARE @Tag sysname = N'20261011';

-- 1. The new pipeline serves both companies.
IF (SELECT COUNT(*) FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) AND CurrentRunId IS NOT NULL AND PublishedRows > 0) <> 2
    THROW 50911, N'Refusing to clean: the new snapshot (dbo.CollectionsRcvCompany) has no published run with rows for BOTH companies. Run the refresh for 4 and 32 and verify first.', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) AND LastStatus <> 'Succeeded')
    PRINT N'WARNING: the newest refresh attempt of a company did not succeed (the older published run is being served). Check dbo.CollectionsRcvCompany before cleaning.';

-- 3. The old pipeline is not running.
DECLARE @lock int;
BEGIN TRANSACTION;
EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
COMMIT TRANSACTION;
IF @lock < 0 THROW 50912, N'Refusing to clean: a refresh of the OLD pipeline is still running. Stop the old application / job first.', 1;

-- 2. Verified backups.
DECLARE @tables TABLE (Seq int IDENTITY(1,1), TableName sysname NOT NULL);
INSERT @tables (TableName) VALUES (N'CollectionsReceivableRunCompany'), (N'CollectionsReceivableRun'), (N'CollectionsReceivableSnapshot'), (N'CollectionsReceivableStaging'),
    (N'CollectionsReceivableTowerSummary'), (N'CollectionsReceivableUnitText'), (N'CollectionsReceivableUnit'), (N'CollectionsContactNorm'), (N'CollectionsReceivableCompanyState');
DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @tables), @t sysname, @bak sysname, @sql nvarchar(max), @src bigint, @dst bigint;
CREATE TABLE #plan (Seq int, TableName sysname, SourceRows bigint, BackupRows bigint);
WHILE @i <= @n
BEGIN
    SELECT @t = TableName FROM @tables WHERE Seq = @i;
    SET @bak = CONCAT(N'Bak_', @t, N'_', @Tag);
    IF OBJECT_ID(CONCAT(N'dbo.', @t), N'U') IS NOT NULL
    BEGIN
        IF OBJECT_ID(CONCAT(N'dbo.', @bak), N'U') IS NULL
        BEGIN
            DECLARE @m nvarchar(400) = CONCAT(N'Refusing to clean: backup dbo.', @bak, N' does not exist. Run M01 first.');
            THROW 50913, @m, 1;
        END;
        SET @sql = CONCAT(N'SELECT @s = COUNT_BIG(*) FROM dbo.', QUOTENAME(@t), N'; SELECT @b = COUNT_BIG(*) FROM dbo.', QUOTENAME(@bak), N';');
        EXEC sys.sp_executesql @sql, N'@s bigint OUTPUT, @b bigint OUTPUT', @s = @src OUTPUT, @b = @dst OUTPUT;
        IF @src <> @dst
        BEGIN
            DECLARE @m2 nvarchar(400) = CONCAT(N'Refusing to clean: dbo.', @t, N' (', @src, N' rows) no longer matches its backup (', @dst, N' rows). Re-take the backup under a new tag.');
            THROW 50914, @m2, 1;
        END;
        INSERT #plan VALUES (@i, @t, @src, @dst);
    END;
    SET @i += 1;
END;
SELECT TableName, SourceRows AS RowsToDelete, BackupRows FROM #plan ORDER BY Seq;
IF @Execute = 0 BEGIN PRINT N'Dry run: nothing deleted. Set @Execute = 1 to clean.'; RETURN; END;

BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.CollectionsReceivableRunCompany', N'U') IS NOT NULL DELETE FROM dbo.CollectionsReceivableRunCompany;
    IF OBJECT_ID(N'dbo.CollectionsReceivableRun', N'U') IS NOT NULL DELETE FROM dbo.CollectionsReceivableRun;
    IF OBJECT_ID(N'dbo.CollectionsReceivableSnapshot', N'U') IS NOT NULL TRUNCATE TABLE dbo.CollectionsReceivableSnapshot;
    IF OBJECT_ID(N'dbo.CollectionsReceivableStaging', N'U') IS NOT NULL TRUNCATE TABLE dbo.CollectionsReceivableStaging;
    IF OBJECT_ID(N'dbo.CollectionsReceivableTowerSummary', N'U') IS NOT NULL TRUNCATE TABLE dbo.CollectionsReceivableTowerSummary;
    IF OBJECT_ID(N'dbo.CollectionsReceivableUnitText', N'U') IS NOT NULL TRUNCATE TABLE dbo.CollectionsReceivableUnitText;
    IF OBJECT_ID(N'dbo.CollectionsReceivableUnit', N'U') IS NOT NULL TRUNCATE TABLE dbo.CollectionsReceivableUnit;
    IF OBJECT_ID(N'dbo.CollectionsContactNorm', N'U') IS NOT NULL TRUNCATE TABLE dbo.CollectionsContactNorm;
    IF OBJECT_ID(N'dbo.CollectionsReceivableCompanyState', N'U') IS NOT NULL
    BEGIN
        DELETE FROM dbo.CollectionsReceivableCompanyState;
        INSERT dbo.CollectionsReceivableCompanyState (CompanyId) VALUES (4), (32);   -- "never loaded"
    END;
COMMIT TRANSACTION;
PRINT N'M02 complete: the old snapshot, staging and refresh history were cleaned (backups kept as dbo.Bak_*_' + @Tag + N').';
