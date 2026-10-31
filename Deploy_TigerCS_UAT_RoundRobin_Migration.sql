/*
================================================================================
 Deploy_TigerCS_UAT_RoundRobin_Migration.sql
 TigerCS Ticketing - UAT: create dbo.DepartmentAssignmentRotations (Round Robin)
--------------------------------------------------------------------------------
 Applies ONE EF Core migration, only if it is not already recorded:

   20261031094748_AddDepartmentAssignmentRotation   (shipped with PR #135)

 What it creates (nothing else):
   dbo.DepartmentAssignmentRotations
     DepartmentId           int              NOT NULL  PK_DepartmentAssignmentRotations
     LastAssignedEmployeeId uniqueidentifier NULL
     UpdatedAtUtc           datetime2        NOT NULL
     RowVersion             rowversion       NOT NULL
     FK_DepartmentAssignmentRotations_Departments_DepartmentId
        -> dbo.Departments (DepartmentId) ON DELETE CASCADE
   plus one row in dbo.__EFMigrationsHistory.

 What it does NOT do:
   * it never modifies, drops or deletes anything in an existing table
     (the only existing table it touches is read-only: Departments is
     referenced by the new foreign key);
   * it inserts no data into the new table - the application creates a
     department's cursor row the first time Round Robin assigns in it.

 Safe to run more than once:
   * if the migration is already in the history table it does nothing;
   * if the table already exists (e.g. a half-applied earlier attempt) it is
     NOT recreated - its shape is checked and only the missing foreign key /
     history row are added;
   * one application lock stops two runs from overlapping.

 One transaction, SET XACT_ABORT ON, TRY/CATCH: any error, failed pre-check or
 failed verification rolls everything back. No GO inside the deployment batch -
 run it as ONE batch in SSMS (Execute). The read-only validation queries after
 the final GO can be run on their own at any time.

 SAFE BY DEFAULT: @DryRun = 1 does every step and every check and then ROLLS
 BACK, so the first Execute keeps nothing. Set @DryRun = 0 to deploy.

 Not executed by the author. Take a backup/snapshot before deploying.
 Rollback instructions are at the end of this file (commented out).
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 60000;

-------------------------------------------------------------------------------
-- Settings
-------------------------------------------------------------------------------
DECLARE @ExpectedDatabase sysname = N'TigerCsTicketing';   -- the script refuses to run in any other database
DECLARE @DryRun bit = 1;                                   -- 1 = apply, verify, then ROLL BACK  |  0 = deploy

-------------------------------------------------------------------------------
-- Constants and state
-------------------------------------------------------------------------------
DECLARE @M nvarchar(150) = N'20261031094748_AddDepartmentAssignmentRotation';
DECLARE @ProductVersion nvarchar(32) = N'10.0.11';

DECLARE @msg nvarchar(2000);
DECLARE @list nvarchar(max);
DECLARE @lockResult int;
DECLARE @committed bit = 0;
DECLARE @recorded bit;
DECLARE @tableExisted bit;
DECLARE @departmentsBefore bigint;
DECLARE @departmentsAfter bigint;
DECLARE @n bigint;
DECLARE @stateRecorded nvarchar(10);
DECLARE @stateTable nvarchar(10);

BEGIN TRY

    ---------------------------------------------------------------------------
    -- 0. Target guard
    ---------------------------------------------------------------------------
    PRINT N'Target: server ' + @@SERVERNAME + N', database ' + DB_NAME() + N', started ' + CONVERT(nvarchar(30), SYSUTCDATETIME(), 126) + N' UTC';

    IF DB_NAME() <> @ExpectedDatabase
    BEGIN
        SET @msg = N'Wrong database: connected to [' + DB_NAME() + N'] but @ExpectedDatabase is [' + @ExpectedDatabase + N']. Nothing was changed.';
        THROW 50001, @msg, 1;
    END;

    ---------------------------------------------------------------------------
    -- 1. PRE-DEPLOYMENT VALIDATION (read-only)
    ---------------------------------------------------------------------------
    PRINT N'--- Pre-deployment validation';

    IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
        THROW 50003, N'dbo.__EFMigrationsHistory does not exist: this is not an EF-managed TigerCS database. Nothing was changed.', 1;

    -- The new foreign key needs dbo.Departments(DepartmentId) to be an int primary key / unique key.
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.Departments') AND c.name = N'DepartmentId' AND t.name = N'int' AND c.is_nullable = 0)
        THROW 50005, N'dbo.Departments.DepartmentId (int NOT NULL) was not found: the table the new foreign key references is missing or different. Nothing was changed.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes AS i
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.Departments') AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1) AND c.name = N'DepartmentId')
        THROW 50007, N'dbo.Departments.DepartmentId is not a primary/unique key, so the foreign key cannot be created. Nothing was changed.', 1;

    -- Informational only: the Round Robin code (#135) was released after these three, so they should already be applied.
    SELECT @list = STUFF((SELECT N', ' + m.MigrationId
        FROM (VALUES (N'20261030061255_AddTicketInteractionHandlers'),
                     (N'20261030062642_AddTicketTransferTracking'),
                     (N'20261030073857_AddTicketCreatedBy')) AS m(MigrationId)
        WHERE NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory AS h WHERE h.MigrationId = m.MigrationId)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
        PRINT N'  WARNING: these earlier migrations are NOT recorded in this database (apply them too, or the application will still fail): ' + @list;

    -- Current state (gathered into variables first: PRINT cannot contain a subquery).
    SET @recorded = CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M) THEN 1 ELSE 0 END;
    SET @tableExisted = CASE WHEN OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL THEN 0 ELSE 1 END;
    SET @stateRecorded = CASE WHEN @recorded = 1 THEN N'recorded' ELSE N'pending' END;
    SET @stateTable = CASE WHEN @tableExisted = 1 THEN N'PRESENT' ELSE N'absent' END;
    PRINT N'  migration ' + @M + N': ' + @stateRecorded;
    PRINT N'  table dbo.DepartmentAssignmentRotations: ' + @stateTable;

    SELECT @departmentsBefore = COUNT_BIG(*) FROM dbo.Departments;
    PRINT N'  Departments rows (must be unchanged afterwards): ' + CONVERT(nvarchar(20), @departmentsBefore);

    ---------------------------------------------------------------------------
    -- 2. DEPLOYMENT (single transaction)
    ---------------------------------------------------------------------------
    BEGIN TRANSACTION;

    EXEC @lockResult = sp_getapplock
        @Resource = N'TigerCS_Deploy_RoundRobin_Migration', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    IF @lockResult < 0
        THROW 50006, N'Another run of this script holds the deployment lock. Nothing was changed.', 1;

    IF @recorded = 1
    BEGIN
        PRINT N'[1/1] ' + @M + N' - already recorded, nothing to create';
    END
    ELSE
    BEGIN
        PRINT N'[1/1] ' + @M + N' - applying';

        IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL
        BEGIN
            EXEC (N'
            CREATE TABLE dbo.DepartmentAssignmentRotations (
                DepartmentId int NOT NULL,
                LastAssignedEmployeeId uniqueidentifier NULL,
                UpdatedAtUtc datetime2 NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_DepartmentAssignmentRotations PRIMARY KEY (DepartmentId),
                CONSTRAINT FK_DepartmentAssignmentRotations_Departments_DepartmentId
                    FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments (DepartmentId) ON DELETE CASCADE
            );');
        END
        ELSE
        BEGIN
            PRINT N'      table already exists - not recreated; completing missing parts only';

            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_DepartmentAssignmentRotations_Departments_DepartmentId' AND parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations'))
            BEGIN
                EXEC (N'ALTER TABLE dbo.DepartmentAssignmentRotations ADD CONSTRAINT FK_DepartmentAssignmentRotations_Departments_DepartmentId
                        FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments (DepartmentId) ON DELETE CASCADE;');
            END;
        END;

        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@M, @ProductVersion);
    END;

    ---------------------------------------------------------------------------
    -- 3. VERIFICATION INSIDE THE TRANSACTION (any failure rolls everything back)
    ---------------------------------------------------------------------------
    PRINT N'--- Verification';

    IF NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = @M)
        THROW 50010, N'Verification failed - the history row is missing.', 1;

    IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NULL
        THROW 50011, N'Verification failed - dbo.DepartmentAssignmentRotations does not exist.', 1;

    DECLARE @col TABLE (ColumnName sysname, TypeName sysname, MaxLength smallint, IsNullable bit);
    INSERT INTO @col VALUES
        (N'DepartmentId',           N'int',              4,  0),
        (N'LastAssignedEmployeeId', N'uniqueidentifier', 16, 1),
        (N'UpdatedAtUtc',           N'datetime2',        8,  0),
        (N'RowVersion',             N'timestamp',        8,  0);   -- rowversion is reported as "timestamp"

    SELECT @list = STUFF((SELECT N', ' + e.ColumnName
        FROM @col AS e
        WHERE NOT EXISTS (
            SELECT 1 FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations') AND c.name = e.ColumnName
              AND t.name = e.TypeName AND c.max_length = e.MaxLength AND c.is_nullable = e.IsNullable)
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    IF @list IS NOT NULL
    BEGIN
        SET @msg = N'Verification failed - column missing or with an unexpected type/size/nullability: ' + @list;
        THROW 50012, @msg, 1;
    END;

    SELECT @n = COUNT_BIG(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations');
    IF @n <> 4
    BEGIN
        SET @msg = N'Verification failed - the table has ' + CONVERT(nvarchar(20), @n) + N' columns, expected 4.';
        THROW 50013, @msg, 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_DepartmentAssignmentRotations' AND type = N'PK' AND parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations'))
        THROW 50014, N'Verification failed - PK_DepartmentAssignmentRotations is missing.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys AS f
        WHERE f.name = N'FK_DepartmentAssignmentRotations_Departments_DepartmentId'
          AND f.parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations')
          AND f.referenced_object_id = OBJECT_ID(N'dbo.Departments')
          AND f.delete_referential_action = 1)           -- 1 = CASCADE
        THROW 50015, N'Verification failed - the foreign key to dbo.Departments (ON DELETE CASCADE) is missing or different.', 1;

    -- Existing data preserved: the referenced table is exactly as it was.
    SELECT @departmentsAfter = COUNT_BIG(*) FROM dbo.Departments;
    IF @departmentsAfter <> @departmentsBefore
    BEGIN
        SET @msg = N'Verification failed - Departments row count changed from ' + CONVERT(nvarchar(20), @departmentsBefore) + N' to ' + CONVERT(nvarchar(20), @departmentsAfter) + N'. Rolled back.';
        THROW 50016, @msg, 1;
    END;

    PRINT N'  all checks passed';

    ---------------------------------------------------------------------------
    -- 4. COMMIT (or roll back for a dry run)
    ---------------------------------------------------------------------------
    IF @DryRun = 1
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT N'DRY RUN: every step and check succeeded and the transaction was ROLLED BACK. Nothing was kept. Set @DryRun = 0 to deploy.';
    END
    ELSE
    BEGIN
        COMMIT TRANSACTION;
        SET @committed = 1;
        PRINT N'COMMITTED. dbo.DepartmentAssignmentRotations exists and the migration is recorded in dbo.__EFMigrationsHistory.';
    END;

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;

    PRINT N'FAILED - the transaction was rolled back and nothing was changed: [' + CONVERT(nvarchar(20), ERROR_NUMBER()) + N'] ' + ERROR_MESSAGE();

    THROW;
END CATCH;
GO

/*
================================================================================
 VALIDATION QUERIES  (read-only; run them before and after the deployment)
 Before: V1 shows no history row and V2 shows no table. After a real deploy
 (@DryRun = 0): V1 shows the row, V2 four columns, V3 PK + FK, V4 zero rows.
================================================================================
*/

-- V1: is the migration recorded, and what is the newest migration?
SELECT MigrationId, ProductVersion FROM dbo.__EFMigrationsHistory
WHERE MigrationId = N'20261031094748_AddDepartmentAssignmentRotation';
SELECT TOP 5 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;

-- V2: the table's columns (expect 4: int NOT NULL, uniqueidentifier NULL, datetime2 NOT NULL, timestamp/rowversion NOT NULL)
SELECT c.column_id, c.name AS ColumnName, t.name AS TypeName, c.max_length, c.is_nullable
FROM sys.columns AS c JOIN sys.types AS t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations') ORDER BY c.column_id;

-- V3: primary key and foreign key (expect PK_... and FK_... with delete_referential_action_desc = CASCADE)
SELECT name, type_desc FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations');
SELECT f.name, OBJECT_NAME(f.referenced_object_id) AS ReferencedTable, f.delete_referential_action_desc
FROM sys.foreign_keys AS f WHERE f.parent_object_id = OBJECT_ID(N'dbo.DepartmentAssignmentRotations');

-- V4: rows in the new table (expect 0 right after deployment; the app adds a row per department on its first Round Robin assignment)
IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NOT NULL
    SELECT COUNT_BIG(*) AS RotationRows FROM dbo.DepartmentAssignmentRotations;
ELSE
    SELECT CAST(NULL AS bigint) AS RotationRows, N'table does not exist yet' AS Note;

-- V5: existing data - row counts of the table the new foreign key points at (compare before vs after)
SELECT (SELECT COUNT_BIG(*) FROM dbo.Departments) AS Departments, (SELECT COUNT_BIG(*) FROM dbo.Tickets) AS Tickets;

/*
================================================================================
 ROLLBACK INSTRUCTIONS  (commented out on purpose)
--------------------------------------------------------------------------------
 Preferred: restore the backup/snapshot taken before deployment.

 Schema rollback drops ONLY the new table. It deletes the saved Round Robin
 cursor positions (harmless: rotation simply starts again from the first
 assignee). Roll the application back first - the current build reads this table
 when it assigns tickets in a department with Round Robin.

 SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 IF OBJECT_ID(N'dbo.DepartmentAssignmentRotations', N'U') IS NOT NULL
     DROP TABLE dbo.DepartmentAssignmentRotations;
 DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261031094748_AddDepartmentAssignmentRotation';
 SELECT TOP 3 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;
 COMMIT TRANSACTION;
================================================================================
*/
