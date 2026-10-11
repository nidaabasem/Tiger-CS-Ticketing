/*
  M02 - CLEAN the previous receivables pipeline's DATA (TigerCsTicketing).  Run only AFTER the new application is deployed and the verification in
  docs/Collections/Receivables-Snapshot.md ("Verify before cleaning") passed on the real server: both companies loaded by the NEW pipeline, fresh, and the pages show the right receivables.

  DRY RUN BY DEFAULT: with @Execute = 0 it performs every check below (holding the same locks), reports what it would delete and deletes nothing.
  With @Execute = 1 it deletes the rows of EXACTLY these 9 tables, in the SAME transaction that holds the locks:
      dbo.CollectionsReceivableRunCompany  dbo.CollectionsReceivableRun  dbo.CollectionsReceivableSnapshot  dbo.CollectionsReceivableStaging
      dbo.CollectionsReceivableTowerSummary  dbo.CollectionsReceivableUnitText  dbo.CollectionsReceivableUnit  dbo.CollectionsContactNorm
      dbo.CollectionsReceivableCompanyState   (emptied, then re-seeded with its two neutral rows 4 and 32 = "never loaded", so the old application could still start)
  The table definitions and the old procedures stay (a rollback needs them); migration/M03 drops them later, optionally.
  It never touches customers, tickets, CRM owner data, dbo.CollectionsTowers, dbo.CollectionsCrmProjectMap, review / campaign / dispatch history, the new dbo.CollectionsRcv* tables or PACT.

  EVERYTHING BELOW RUNS INSIDE ONE TRANSACTION, WITH THE LOCKS HELD FROM THE FIRST CHECK TO THE COMMIT (nothing is released between the verification and the delete):
    L1. the OLD pipeline's lock 'Collections.ReceivablesRefresh' (no old refresh is executing and none can start meanwhile);
    L2. the NEW pipeline's company locks 'Collections.Rcv.4' / 'Collections.Rcv.32' (no refresh of the new snapshot can swap a copy between the verification and the delete).
  Then, still under those locks, it REFUSES (THROW, nothing deleted) unless ALL of these hold:
    C1. both companies have a published run (CurrentRunId) whose run row is Succeeded, and the number of stored lines of that run equals PublishedRows (> 0);
    C2. the newest attempt of each company succeeded (LastStatus = Succeeded, nothing Running);
    C3. each published copy is fresh: LastSuccessUtc is not older than @MaxAgeMinutes (180 = the application's MaxAgeMinutes);
    C4. every table above that exists has a verified backup dbo.Bak_<table>_<tag> with exactly the same row count (M01).
  Use the same @Tag as M01.  Needs the new pipeline (R001-R004) to exist.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Execute bit = 0;           -- 1 = delete
DECLARE @Tag sysname = N'20261011';
DECLARE @MaxAgeMinutes int = 180;

DECLARE @lock int, @m nvarchar(500);
BEGIN TRANSACTION;

-- L1 / L2: take and HOLD the locks until COMMIT / ROLLBACK (Transaction-owned: released only when this transaction ends).
EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
IF @lock < 0 THROW 50912, N'Refusing to clean: a refresh of the OLD pipeline is running (its lock is held). Stop the old application / job first.', 1;
EXEC @lock = sys.sp_getapplock @Resource = N'Collections.Rcv.4', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
IF @lock < 0 THROW 50915, N'Refusing to clean: a refresh of the NEW snapshot for company 4 is running. Wait for it to finish and run this script again.', 1;
EXEC @lock = sys.sp_getapplock @Resource = N'Collections.Rcv.32', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
IF @lock < 0 THROW 50915, N'Refusing to clean: a refresh of the NEW snapshot for company 32 is running. Wait for it to finish and run this script again.', 1;

-- C1 / C2 / C3: the new copies are valid, the newest attempts succeeded, and they are fresh.
IF OBJECT_ID(N'dbo.CollectionsRcvCompany', N'U') IS NULL THROW 50916, N'Refusing to clean: the new snapshot tables do not exist (run R001-R004 first).', 1;
IF (SELECT COUNT(*) FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) AND CurrentRunId IS NOT NULL AND PublishedRows > 0) <> 2
    THROW 50911, N'Refusing to clean: the new snapshot has no published run with rows for BOTH companies (4 and 32). Refresh both and verify first.', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsRcvCompany c WHERE c.CompanyId IN (4, 32)
            AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsRcvRun r WHERE r.RunId = c.CurrentRunId AND r.CompanyId = c.CompanyId AND r.Status = 'Succeeded'))
    THROW 50917, N'Refusing to clean: the published run of a company is not a Succeeded run in dbo.CollectionsRcvRun.', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsRcvCompany c WHERE c.CompanyId IN (4, 32)
            AND c.PublishedRows <> (SELECT COUNT_BIG(*) FROM dbo.CollectionsRcvLine l WHERE l.CompanyId = c.CompanyId AND l.RunId = c.CurrentRunId))
    THROW 50918, N'Refusing to clean: the stored lines of a published run do not match its PublishedRows (the copy is incomplete or damaged).', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) AND LastStatus <> 'Succeeded')
    THROW 50919, N'Refusing to clean: the newest refresh attempt of a company did not succeed (see dbo.CollectionsRcvCompany.LastStatus / LastError). Fix it and refresh before cleaning.', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) AND (LastSuccessUtc IS NULL OR LastSuccessUtc < DATEADD(MINUTE, -@MaxAgeMinutes, SYSUTCDATETIME())))
    THROW 50920, N'Refusing to clean: a published copy is older than @MaxAgeMinutes (or never succeeded). Refresh both companies and run this script again.', 1;

-- C4: verified backups.
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
            SET @m = CONCAT(N'Refusing to clean: backup dbo.', @bak, N' does not exist. Run M01 first.');
            THROW 50913, @m, 1;
        END;
        SET @sql = CONCAT(N'SELECT @s = COUNT_BIG(*) FROM dbo.', QUOTENAME(@t), N'; SELECT @b = COUNT_BIG(*) FROM dbo.', QUOTENAME(@bak), N';');
        EXEC sys.sp_executesql @sql, N'@s bigint OUTPUT, @b bigint OUTPUT', @s = @src OUTPUT, @b = @dst OUTPUT;
        IF @src <> @dst
        BEGIN
            SET @m = CONCAT(N'Refusing to clean: dbo.', @t, N' (', @src, N' rows) no longer matches its backup (', @dst, N' rows). Re-take the backup under a new tag.');
            THROW 50914, @m, 1;
        END;
        INSERT #plan VALUES (@i, @t, @src, @dst);
    END;
    SET @i += 1;
END;
SELECT c.CompanyId, c.PublishedRows, c.LastSuccessUtc, DATEDIFF(MINUTE, c.LastSuccessUtc, SYSUTCDATETIME()) AS AgeMinutes, c.LastStatus FROM dbo.CollectionsRcvCompany c WHERE c.CompanyId IN (4, 32) ORDER BY c.CompanyId;
SELECT TableName, SourceRows AS RowsToDelete, BackupRows FROM #plan ORDER BY Seq;

IF @Execute = 0
BEGIN
    ROLLBACK TRANSACTION;           -- releases the locks; nothing was changed
    PRINT N'Dry run: every check passed, nothing deleted. Set @Execute = 1 to clean.';
    RETURN;
END;

-- Still inside the transaction that holds L1 and L2.
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
COMMIT TRANSACTION;                 -- the locks are released only here, after the delete
PRINT CONCAT(N'M02 complete: the old snapshot, staging and refresh history were cleaned (backups kept as dbo.Bak_*_', @Tag, N').');
