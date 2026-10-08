/*
  V001 - Reconcile the manually created dbo.CollectionsTowers table (TigerCsTicketing).
  NON-DESTRUCTIVE: never removes rows, never re-seeds the identity or changes column types of an existing table, and never changes
  identity values (TowerId is a local identity and is NOT assumed to equal a CRM project id).
  Idempotent: safe to run repeatedly.

  Required columns (existing table): TowerId, TowerNumber, TowerName, CompanyId, IsActive.
  TowerNumber may be numeric or text in the manually created table; every consumer compares
  LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))), so both work.

  What it does
    1. Creates the table ONLY if it does not exist (empty; no seed rows are invented).
    2. Fails fast if a required column is missing (it does not guess a fix).
    3. Adds a unique index on (CompanyId, TowerNumber) when no duplicates exist; otherwise a non-unique index and a warning.
    4. Prints reconciliation reports (read-only SELECTs): duplicates, unsupported companies, padded/odd numbers,
       expected seed towers that are missing, and the current towers.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.CollectionsTowers', N'U') IS NULL
BEGIN
    PRINT N'dbo.CollectionsTowers not found: creating an empty table (no seed data is inserted by this script).';
    CREATE TABLE dbo.CollectionsTowers
    (
        TowerId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionsTowers PRIMARY KEY,
        TowerNumber nvarchar(20)      NOT NULL,
        TowerName   nvarchar(200)     NOT NULL,
        CompanyId   int               NOT NULL,
        IsActive    bit               NOT NULL CONSTRAINT DF_CollectionsTowers_IsActive DEFAULT (1)
    );
END;

DECLARE @missing nvarchar(400) =
(
    SELECT STRING_AGG(r.ColumnName, N', ')
    FROM (VALUES (N'TowerId'), (N'TowerNumber'), (N'TowerName'), (N'CompanyId'), (N'IsActive')) r(ColumnName)
    WHERE COL_LENGTH(N'dbo.CollectionsTowers', r.ColumnName) IS NULL
);
IF @missing IS NOT NULL
BEGIN
    DECLARE @msg nvarchar(500) = CONCAT(N'dbo.CollectionsTowers is missing required column(s): ', @missing, N'. Fix the table manually; this script does not alter an existing table.');
    THROW 50101, @msg, 1;
END;

-- Lookup index used by the snapshot read procedure (CompanyId + TowerNumber).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsTowers')
               AND name IN (N'UX_CollectionsTowers_Company_TowerNumber', N'IX_CollectionsTowers_Company_TowerNumber'))
BEGIN
    DECLARE @dupes int;
    EXEC sys.sp_executesql
        N'SELECT @n = COUNT(*) FROM (SELECT CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) AS n
                                     FROM dbo.CollectionsTowers GROUP BY CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber)))
                                     HAVING COUNT(*) > 1) d',
        N'@n int OUTPUT', @n = @dupes OUTPUT;

    IF @dupes = 0
    BEGIN
        -- Unique only when the column is directly indexable (numeric or bounded text); otherwise fall through to the warning.
        BEGIN TRY
            EXEC (N'CREATE UNIQUE INDEX UX_CollectionsTowers_Company_TowerNumber ON dbo.CollectionsTowers (CompanyId, TowerNumber)');
            PRINT N'Created UX_CollectionsTowers_Company_TowerNumber.';
        END TRY
        BEGIN CATCH
            PRINT N'Could not create the unique index (' + ERROR_MESSAGE() + N'). Continuing without it.';
        END CATCH;
    END
    ELSE
    BEGIN
        PRINT N'WARNING: duplicate (CompanyId, TowerNumber) pairs exist - unique index NOT created. See report 1. '
            + N'The read procedure picks the active, lowest TowerId row deterministically until this is fixed.';
        BEGIN TRY
            EXEC (N'CREATE INDEX IX_CollectionsTowers_Company_TowerNumber ON dbo.CollectionsTowers (CompanyId, TowerNumber)');
        END TRY
        BEGIN CATCH
            PRINT N'Could not create the lookup index (' + ERROR_MESSAGE() + N').';
        END CATCH;
    END;
END;

-- ===== Reconciliation reports (read-only) =====
PRINT N'--- 1. Duplicate (CompanyId, TowerNumber) pairs (must be empty) ---';
EXEC sys.sp_executesql N'
SELECT CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) AS TowerNumber, COUNT(*) AS Rows_, MIN(TowerId) AS FirstTowerId
FROM dbo.CollectionsTowers
GROUP BY CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber)))
HAVING COUNT(*) > 1;';

PRINT N'--- 2. Towers whose CompanyId is not a supported PACT company (4 or 32) ---';
EXEC sys.sp_executesql N'SELECT TowerId, TowerNumber, TowerName, CompanyId FROM dbo.CollectionsTowers WHERE CompanyId NOT IN (4, 32);';

PRINT N'--- 3. TowerNumber values with padding, a TP prefix, a hyphen or leading zeros (will not match unit-derived numbers) ---';
EXEC sys.sp_executesql N'
SELECT TowerId, TowerNumber, CompanyId
FROM dbo.CollectionsTowers
WHERE CONVERT(nvarchar(20), TowerNumber) <> LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber)))
   OR UPPER(LTRIM(CONVERT(nvarchar(20), TowerNumber))) LIKE N''TP%''
   OR CONVERT(nvarchar(20), TowerNumber) LIKE N''%-%''
   OR LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) LIKE N''0%'';';

PRINT N'--- 4. Company 32 expected towers (Faradis 127, Al Ghaf 140): any listed here are MISSING or on the wrong company ---';
EXEC sys.sp_executesql N'
SELECT e.TowerNumber AS ExpectedTowerNumber, e.Name AS ExpectedName
FROM (VALUES (N''127'', N''Faradis''), (N''140'', N''Al Ghaf'')) e(TowerNumber, Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.CollectionsTowers t
                  WHERE t.CompanyId = 32 AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = e.TowerNumber);';

PRINT N'--- 5. Inactive towers (hidden from the dropdown; their receivables stay in "All towers" and are reported as InactiveTower) ---';
EXEC sys.sp_executesql N'SELECT TowerId, TowerNumber, TowerName, CompanyId FROM dbo.CollectionsTowers WHERE IsActive = 0;';

PRINT N'--- 6. Current towers ---';
EXEC sys.sp_executesql N'SELECT TowerId, TowerNumber, TowerName, CompanyId, IsActive FROM dbo.CollectionsTowers ORDER BY CompanyId, TRY_CONVERT(int, TowerNumber), TowerNumber;';
PRINT N'V001 complete. Tower 119 is intentionally NOT seeded: unmatched towers are reported by the receivables snapshot, not invented.';
