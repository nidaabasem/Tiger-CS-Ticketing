/*
  M01 - BACKUP of the previous receivables pipeline's tables (TigerCsTicketing).  Run BEFORE the new application is deployed, after R001-R004.

  Copies each table below, by name, into dbo.Bak_<table>_<tag> (SELECT ... INTO).  Never overwrites an existing backup (it reports and skips it), never changes or deletes a source table,
  and verifies that every backup has exactly the source's row count.  Customer data, tickets, CRM owner data, tower mapping, campaign / review / dispatch history and PACT are not touched.

  Tables backed up (exactly these 9):
      dbo.CollectionsReceivableRun            dbo.CollectionsReceivableRunCompany     dbo.CollectionsReceivableCompanyState
      dbo.CollectionsReceivableSnapshot       dbo.CollectionsReceivableStaging        dbo.CollectionsReceivableTowerSummary
      dbo.CollectionsReceivableUnit           dbo.CollectionsReceivableUnitText       dbo.CollectionsContactNorm

  It is NOT a substitute for a full database backup: take that first (DBA, outside this script).  Space needed is about the size of dbo.CollectionsReceivableSnapshot.
  Restore: migration/M90__rollback_restore_legacy.sql.  Edit @Tag if you run this on another day (the cleanup and rollback scripts must use the same tag).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Tag sysname = N'20261011';

DECLARE @tables TABLE (Seq int IDENTITY(1,1), TableName sysname NOT NULL);
INSERT @tables (TableName) VALUES (N'CollectionsReceivableRun'), (N'CollectionsReceivableRunCompany'), (N'CollectionsReceivableCompanyState'),
    (N'CollectionsReceivableSnapshot'), (N'CollectionsReceivableStaging'), (N'CollectionsReceivableTowerSummary'),
    (N'CollectionsReceivableUnit'), (N'CollectionsReceivableUnitText'), (N'CollectionsContactNorm');

DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @tables), @t sysname, @bak sysname, @sql nvarchar(max), @src bigint, @dst bigint;
WHILE @i <= @n
BEGIN
    SELECT @t = TableName FROM @tables WHERE Seq = @i;
    SET @bak = CONCAT(N'Bak_', @t, N'_', @Tag);
    IF OBJECT_ID(CONCAT(N'dbo.', @t), N'U') IS NULL
        PRINT CONCAT(N'dbo.', @t, N' does not exist on this server: nothing to back up.');
    ELSE IF OBJECT_ID(CONCAT(N'dbo.', @bak), N'U') IS NOT NULL
        PRINT CONCAT(N'dbo.', @bak, N' already exists: kept as it is (never overwritten).');
    ELSE
    BEGIN
        SET @sql = CONCAT(N'SELECT * INTO dbo.', QUOTENAME(@bak), N' FROM dbo.', QUOTENAME(@t), N';');
        EXEC sys.sp_executesql @sql;
        PRINT CONCAT(N'dbo.', @t, N' -> dbo.', @bak);
    END;
    SET @i += 1;
END;

-- Verification: every existing source table has a backup with the same row count.
CREATE TABLE #check (TableName sysname, BackupName sysname, SourceRows bigint, BackupRows bigint);
SET @i = 1;
WHILE @i <= @n
BEGIN
    SELECT @t = TableName FROM @tables WHERE Seq = @i;
    SET @bak = CONCAT(N'Bak_', @t, N'_', @Tag);
    IF OBJECT_ID(CONCAT(N'dbo.', @t), N'U') IS NOT NULL
    BEGIN
        SET @sql = CONCAT(N'SELECT @s = COUNT_BIG(*) FROM dbo.', QUOTENAME(@t), N'; SELECT @b = CASE WHEN OBJECT_ID(N''dbo.', @bak, N''', N''U'') IS NULL THEN -1 ELSE (SELECT COUNT_BIG(*) FROM dbo.', QUOTENAME(@bak), N') END;');
        EXEC sys.sp_executesql @sql, N'@s bigint OUTPUT, @b bigint OUTPUT', @s = @src OUTPUT, @b = @dst OUTPUT;
        INSERT #check VALUES (@t, @bak, @src, @dst);
    END;
    SET @i += 1;
END;
SELECT TableName, BackupName, SourceRows, BackupRows, CASE WHEN SourceRows = BackupRows THEN 'OK' ELSE 'MISMATCH' END AS Result FROM #check ORDER BY TableName;
IF EXISTS (SELECT 1 FROM #check WHERE SourceRows <> BackupRows)
    THROW 50901, N'A backup does not match its source table. Do NOT run the cleanup. Investigate (rows may have changed while copying: the old refresh job should be stopped first).', 1;
PRINT N'M01 complete: all backups verified.';
