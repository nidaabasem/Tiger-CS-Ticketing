/*
  M03 - OPTIONAL, LATER: drop the previous pipeline's objects (TigerCsTicketing).  Not part of the required deployment.  Run only after the new pipeline has been stable
  for the retention period you choose (after this, migration/M90 can no longer restore the old pipeline, because the old tables and procedures are gone; the Bak_* tables are
  kept and are NEVER dropped by this script).

  DRY RUN BY DEFAULT (@Execute = 0 lists what exists).  Drops exactly these objects, by name:
    procedures  usp_Collections_RefreshReceivables  usp_Collections_PublishReceivablesStaging  usp_Collections_RecordReceivablesFailure  usp_Collections_CloseReceivablesRun
                usp_Collections_GetCoverage  usp_Collections_GetUnmatchedTowers  usp_Collections_GetReceivables  usp_Collections_GetReceivablesPage  usp_Collections_GetInstalmentsPage
                usp_Collections_GetTowers  usp_Collections_GetCampaignUnits  usp_Collections_SetContactNorms  usp_Collections_SyncUnitContacts
                usp_Collections_GetInstalmentUnitsPage  usp_Collections_GetInstalmentMonths  usp_Collections_GetUnitReceivables
    tables      CollectionsReceivableRunCompany  CollectionsReceivableRun  CollectionsReceivableCompanyState  CollectionsReceivableSnapshot  CollectionsReceivableStaging
                CollectionsReceivableTowerSummary  CollectionsReceivableUnitText  CollectionsReceivableUnit  CollectionsContactNorm
  Refuses to drop a table that still holds rows (run M02 first).  Nothing else is touched.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Execute bit = 0;

DECLARE @objects TABLE (Seq int IDENTITY(1,1), Kind char(1), Name sysname);
INSERT @objects (Kind, Name) VALUES
 ('P', N'usp_Collections_RefreshReceivables'), ('P', N'usp_Collections_PublishReceivablesStaging'), ('P', N'usp_Collections_RecordReceivablesFailure'), ('P', N'usp_Collections_CloseReceivablesRun'),
 ('P', N'usp_Collections_GetCoverage'), ('P', N'usp_Collections_GetUnmatchedTowers'), ('P', N'usp_Collections_GetReceivables'), ('P', N'usp_Collections_GetReceivablesPage'),
 ('P', N'usp_Collections_GetInstalmentsPage'), ('P', N'usp_Collections_GetTowers'), ('P', N'usp_Collections_GetCampaignUnits'), ('P', N'usp_Collections_SetContactNorms'),
 ('P', N'usp_Collections_SyncUnitContacts'), ('P', N'usp_Collections_GetInstalmentUnitsPage'), ('P', N'usp_Collections_GetInstalmentMonths'), ('P', N'usp_Collections_GetUnitReceivables'),
 ('T', N'CollectionsReceivableRunCompany'), ('T', N'CollectionsReceivableRun'), ('T', N'CollectionsReceivableCompanyState'), ('T', N'CollectionsReceivableSnapshot'),
 ('T', N'CollectionsReceivableStaging'), ('T', N'CollectionsReceivableTowerSummary'), ('T', N'CollectionsReceivableUnitText'), ('T', N'CollectionsReceivableUnit'), ('T', N'CollectionsContactNorm');

DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @objects), @k char(1), @name sysname, @sql nvarchar(max), @rows bigint, @type nvarchar(2);
WHILE @i <= @n
BEGIN
    SELECT @k = Kind, @name = Name FROM @objects WHERE Seq = @i;
    SET @type = CASE @k WHEN 'P' THEN N'P' ELSE N'U' END;
    IF OBJECT_ID(CONCAT(N'dbo.', @name), @type) IS NOT NULL
    BEGIN
        IF @k = 'T'
        BEGIN
            SET @sql = CONCAT(N'SELECT @r = COUNT_BIG(*) FROM dbo.', QUOTENAME(@name), N';');
            EXEC sys.sp_executesql @sql, N'@r bigint OUTPUT', @r = @rows OUTPUT;
            IF @rows > 0
            BEGIN
                DECLARE @m nvarchar(400) = CONCAT(N'Refusing to drop dbo.', @name, N': it still holds ', @rows, N' rows. Run M02 first.');
                THROW 50921, @m, 1;
            END;
        END;
        PRINT CONCAT(CASE WHEN @Execute = 1 THEN N'DROP ' ELSE N'would drop ' END, CASE @k WHEN 'P' THEN N'PROCEDURE ' ELSE N'TABLE ' END, N'dbo.', @name);
    END;
    SET @i += 1;
END;
IF @Execute = 0 BEGIN PRINT N'Dry run: nothing dropped. Set @Execute = 1 to drop.'; RETURN; END;

BEGIN TRANSACTION;
    SET @i = 1;
    WHILE @i <= @n
    BEGIN
        SELECT @k = Kind, @name = Name FROM @objects WHERE Seq = @i;
        SET @type = CASE @k WHEN 'P' THEN N'P' ELSE N'U' END;
        IF OBJECT_ID(CONCAT(N'dbo.', @name), @type) IS NOT NULL
        BEGIN
            SET @sql = CONCAT(N'DROP ', CASE @k WHEN 'P' THEN N'PROCEDURE ' ELSE N'TABLE ' END, N'dbo.', QUOTENAME(@name), N';');
            EXEC sys.sp_executesql @sql;
        END;
        SET @i += 1;
    END;
COMMIT TRANSACTION;
PRINT N'M03 complete.';
