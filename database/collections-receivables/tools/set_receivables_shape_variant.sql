/*
  Remember the PACT result layout of ONE company, so the refresh tries it first (V005). Changes only CollectionsReceivableCompanyState.ShapeVariant.
  Dry run by default. Use after tools/test_snapshot_fetch_path.sql section 3 succeeded with that layout. Never changes the snapshot, coverage, companies or settings.
*/
SET NOCOUNT ON;
DECLARE @CompanyId int = 32;
DECLARE @ShapeVariant tinyint = 3;       -- 1..4 (deployed procedures), 5..6 (companion V2 procedures); the number that worked in test section 3
DECLARE @Apply bit = 0;

IF @CompanyId NOT IN (4, 32) OR @ShapeVariant NOT BETWEEN 1 AND 6 BEGIN SELECT N'STOP: company must be 4 or 32, layout 1..6.' AS Result; RETURN; END;
DECLARE @lock int;
EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
IF @lock < 0 BEGIN SELECT N'STOP: a refresh holds the lock right now. Nothing was changed.' AS Result; RETURN; END;
BEGIN TRY
    SELECT CompanyId, ShapeVariant AS Before, @ShapeVariant AS After, CASE WHEN @Apply = 1 THEN N'APPLIED' ELSE N'dry run' END AS Action FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @CompanyId;
    IF @Apply = 1 UPDATE dbo.CollectionsReceivableCompanyState SET ShapeVariant = @ShapeVariant WHERE CompanyId = @CompanyId;
END TRY
BEGIN CATCH
    SELECT N'ERROR - nothing changed' AS Result, ERROR_NUMBER() AS ErrorNumber, ERROR_MESSAGE() AS ErrorMessage;
END CATCH;
EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';
