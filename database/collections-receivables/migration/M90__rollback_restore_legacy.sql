/*
  M90 - ROLLBACK: put the previous pipeline's data back (TigerCsTicketing).  Use only when the new application is being rolled back to the previous release.

  Order:  1. stop the new application   2. run this script   3. deploy the previous release package   4. start it (its recurring job "collections-receivables-refresh" is registered again at start-up).

  Restores the 9 tables of M01 from dbo.Bak_<table>_<tag>, in one transaction (the old procedures are still deployed unless M03 was run - then this script stops).
  Refuses while the legacy tables hold rows, unless @OverwriteExisting = 1 (they are then emptied first).  Keeps the new dbo.CollectionsRcv* tables as they are.
  Also removes the new pipeline's two Hangfire recurring-job entries (collections-receivables-refresh-4 / -32) from the HangfireSla schema: the previous release does not know their
  (company id) signature and would fail every tick.  Nothing else is touched.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Tag sysname = N'20261011';
DECLARE @OverwriteExisting bit = 0;

DECLARE @tables TABLE (Seq int IDENTITY(1,1), TableName sysname NOT NULL);   -- parents first for the insert; the delete runs in reverse
INSERT @tables (TableName) VALUES (N'CollectionsReceivableCompanyState'), (N'CollectionsReceivableRun'), (N'CollectionsReceivableRunCompany'), (N'CollectionsReceivableSnapshot'),
    (N'CollectionsReceivableStaging'), (N'CollectionsReceivableTowerSummary'), (N'CollectionsReceivableUnit'), (N'CollectionsReceivableUnitText'), (N'CollectionsContactNorm');
DECLARE @i int, @n int = (SELECT COUNT(*) FROM @tables), @t sysname, @bak sysname, @sql nvarchar(max), @cols nvarchar(max), @rows bigint, @identity bit, @m nvarchar(400);

-- Preconditions: the old tables and every backup exist.
SET @i = 1;
WHILE @i <= @n
BEGIN
    SELECT @t = TableName FROM @tables WHERE Seq = @i;
    SET @bak = CONCAT(N'Bak_', @t, N'_', @Tag);
    IF OBJECT_ID(CONCAT(N'dbo.', @t), N'U') IS NULL BEGIN SET @m = CONCAT(N'dbo.', @t, N' does not exist (M03 was run?). The old pipeline cannot be restored in place.'); THROW 50931, @m, 1; END;
    IF OBJECT_ID(CONCAT(N'dbo.', @bak), N'U') IS NULL BEGIN SET @m = CONCAT(N'Backup dbo.', @bak, N' does not exist.'); THROW 50932, @m, 1; END;
    -- the two neutral "never loaded" rows that M02 re-seeds in CompanyState do not count as data
    SET @sql = CONCAT(N'SELECT @r = COUNT_BIG(*) FROM dbo.', QUOTENAME(@t), CASE WHEN @t = N'CollectionsReceivableCompanyState' THEN N' WHERE CurrentRunId IS NOT NULL OR LastAttemptStatus <> ''Never''' ELSE N'' END, N';');
    EXEC sys.sp_executesql @sql, N'@r bigint OUTPUT', @r = @rows OUTPUT;
    IF @rows > 0 AND @OverwriteExisting = 0 BEGIN SET @m = CONCAT(N'dbo.', @t, N' already holds ', @rows, N' rows. Set @OverwriteExisting = 1 to replace them with the backup.'); THROW 50933, @m, 1; END;
    SET @i += 1;
END;

BEGIN TRANSACTION;
    -- empty the old tables (children first)
    DELETE FROM dbo.CollectionsReceivableRunCompany;
    DELETE FROM dbo.CollectionsReceivableRun;
    DELETE FROM dbo.CollectionsReceivableSnapshot;
    DELETE FROM dbo.CollectionsReceivableStaging;
    DELETE FROM dbo.CollectionsReceivableTowerSummary;
    DELETE FROM dbo.CollectionsReceivableUnitText;
    DELETE FROM dbo.CollectionsReceivableUnit;
    DELETE FROM dbo.CollectionsContactNorm;
    DELETE FROM dbo.CollectionsReceivableCompanyState;

    SET @i = 1;
    WHILE @i <= @n
    BEGIN
        SELECT @t = TableName FROM @tables WHERE Seq = @i;
        SET @bak = CONCAT(N'Bak_', @t, N'_', @Tag);
        SELECT @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
          FROM sys.columns c WHERE c.object_id = OBJECT_ID(CONCAT(N'dbo.', @bak)) AND c.is_computed = 0;
        SET @identity = CASE WHEN EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(CONCAT(N'dbo.', @t)) AND is_identity = 1) THEN 1 ELSE 0 END;
        SET @sql = CONCAT(CASE WHEN @identity = 1 THEN CONCAT(N'SET IDENTITY_INSERT dbo.', QUOTENAME(@t), N' ON; ') ELSE N'' END,
                          N'INSERT dbo.', QUOTENAME(@t), N' (', @cols, N') SELECT ', @cols, N' FROM dbo.', QUOTENAME(@bak), N'; ',
                          CASE WHEN @identity = 1 THEN CONCAT(N'SET IDENTITY_INSERT dbo.', QUOTENAME(@t), N' OFF;') ELSE N'' END);
        EXEC sys.sp_executesql @sql;
        PRINT CONCAT(N'restored dbo.', @t, N' from dbo.', @bak);
        SET @i += 1;
    END;

    -- The previous release cannot run the new per-company recurring jobs.
    IF OBJECT_ID(N'HangfireSla.[Set]', N'U') IS NOT NULL
        DELETE FROM HangfireSla.[Set] WHERE [Key] = N'recurring-jobs' AND [Value] IN (N'collections-receivables-refresh-4', N'collections-receivables-refresh-32');
    IF OBJECT_ID(N'HangfireSla.Hash', N'U') IS NOT NULL
        DELETE FROM HangfireSla.Hash WHERE [Key] IN (N'recurring-job:collections-receivables-refresh-4', N'recurring-job:collections-receivables-refresh-32');
COMMIT TRANSACTION;
PRINT N'M90 complete: deploy the previous release package now.';
