/*
  V005 - Refresh the local receivables snapshot from PACT through the linked server.

  Called by the application background job (Hangfire recurring job "collections-receivables-refresh") and manually from SSMS:
      EXEC dbo.usp_Collections_RefreshReceivables @TriggerSource = N'Manual';

  Source (confirmed setup): [10.10.10.94].[PACTRPT].[dbo].[p4AccountReceivables] / [p32AccountReceivables]
  Edit @LinkedServer / @RemoteDatabase below if the topology changes. Requirements: linked server with RPC Out and Data Access
  enabled, a login mapping for the TigerCsTicketing login, EXECUTE on both procedures.

  * INSERT ... EXEC over a linked server normally enlists in a DISTRIBUTED transaction (MSDTC). SET REMOTE_PROC_TRANSACTIONS OFF
    avoids that. If error 8501/7391 still appears, MSDTC or the linked-server "Enable Promotion of Distributed Transactions"
    option must be addressed - see docs/Collections/Receivables-Snapshot.md.
  * @SourceMinAmount defaults to 0 on purpose: MinAmount is an int filter inside PACT (Amount >= @MinAmount) and a higher value would
    permanently drop small positive balances. Zero-balance rows are removed during staging validation, not in PACT.
  * @StartDate/@EndDate only filter the instalment DueDate inside PACT; the ledger/allocation arithmetic is unaffected by them
    (Receivables-Source-Review.md section 1), so a wide window never truncates historical accounting.
  * The result-set SHAPE of the deployed procedures must be confirmed (run tests/probe_pact_result_shape.sql). Four column-list
    variants are tried (remembered variant first); a shape mismatch (error 213) falls through to the next one:
        1 CompanyID,ProjectCode,UnitCode,TenantID,FullName,Mobile,Email,UnitID,VoucherNumber,ChequeNumber,DueDate,Amount,Status
        2 same without Status        3 same without ProjectCode        4 without ProjectCode and Status
    Every variant must already be in the procedure's column ORDER; an unknown order fails with a clear message and publishes nothing.
  * Overlap protection: a session-scoped application lock. A second concurrent call returns RunStatus = 'AlreadyRunning'.
  * Companies are processed independently; one failing company never blocks or rolls back the other.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_RefreshReceivables
    @TriggerSource      nvarchar(50) = N'Manual',
    @CompanyId          int          = NULL,           -- NULL = both companies
    @SourceFromDate     date         = '20000101',
    @SourceThroughDate  date         = '20991231',
    @SourceMinAmount    int          = 0,
    @MaxRawRows         int          = 1000000,
    @MaxShrinkPercent   int          = 60,
    @ShrinkGuardMinRows int          = 200,
    @AllowLargeShrink   bit          = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    SET REMOTE_PROC_TRANSACTIONS OFF;

    DECLARE @LinkedServer sysname = N'10.10.10.94', @RemoteDatabase sysname = N'PACTRPT';

    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50001, N'Company must be 4, 32 or NULL.', 1;
    IF @SourceFromDate > @SourceThroughDate THROW 50002, N'@SourceFromDate must not be after @SourceThroughDate.', 1;

    DECLARE @lock int;
    EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive',
                                   @LockOwner = N'Session', @LockTimeout = 0;
    IF @lock < 0
    BEGIN
        SELECT CAST(NULL AS uniqueidentifier) AS RunId, 'AlreadyRunning' AS RunStatus,
               N'Another refresh is already running; this call did nothing.' AS Message;
        SELECT CAST(NULL AS int) AS CompanyId, CAST(NULL AS varchar(20)) AS Status, CAST(NULL AS int) AS RawRows, CAST(NULL AS int) AS PublishedRows,
               CAST(NULL AS int) AS ExcludedZeroRows, CAST(NULL AS int) AS ExcludedInvalidUnitRows, CAST(NULL AS int) AS ExcludedInvalidIdentityRows,
               CAST(NULL AS int) AS ErrorNumber, CAST(NULL AS nvarchar(1000)) AS ErrorMessage WHERE 1 = 0;
        RETURN;
    END;

    DECLARE @RunId uniqueidentifier = NEWID(), @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @startDt datetime = CAST(@SourceFromDate AS datetime);
    DECLARE @endDt   datetime = DATEADD(MILLISECOND, -3, DATEADD(DAY, 1, CAST(@SourceThroughDate AS datetime)));  -- 23:59:59.997

    BEGIN TRY
        -- We hold the lock, so any 'Running' row belongs to a crashed earlier attempt.
        UPDATE dbo.CollectionsReceivableRun SET Status = 'Abandoned', FinishedUtc = @now, Message = N'Superseded by a later refresh.' WHERE Status = 'Running';
        INSERT dbo.CollectionsReceivableRun (RunId, TriggerSource, RequestedCompanyId, StartedUtc, Status)
        VALUES (@RunId, @TriggerSource, @CompanyId, @now, 'Running');

        DECLARE @c int, @pref tinyint, @n int, @v tinyint, @ok bit, @sql nvarchar(max), @cols nvarchar(max), @proc nvarchar(400),
                @errNo int, @errMsg nvarchar(4000), @statusPresent bit;

        DECLARE company_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT v.CompanyId FROM (VALUES (4), (32)) v (CompanyId) WHERE @CompanyId IS NULL OR v.CompanyId = @CompanyId ORDER BY v.CompanyId;
        OPEN company_cursor;
        FETCH NEXT FROM company_cursor INTO @c;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            INSERT dbo.CollectionsReceivableRunCompany (RunId, CompanyId, StartedUtc, Status) VALUES (@RunId, @c, SYSUTCDATETIME(), 'Running');
            DELETE FROM dbo.CollectionsReceivableStaging;

            SET @proc = CONCAT(QUOTENAME(@LinkedServer), N'.', QUOTENAME(@RemoteDatabase), N'.', QUOTENAME(N'dbo'), N'.',
                               QUOTENAME(CONCAT(N'p', @c, N'AccountReceivables')));
            SELECT @pref = ISNULL(ShapeVariant, 1) FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @c;
            SELECT @n = 0, @ok = 0, @errNo = NULL, @errMsg = NULL, @v = NULL;

            WHILE @ok = 0 AND @n < 4
            BEGIN
                SELECT @v = o.v
                  FROM (SELECT x.v, ROW_NUMBER() OVER (ORDER BY CASE WHEN x.v = @pref THEN 0 ELSE 1 END, x.v) AS rn
                          FROM (VALUES (1), (2), (3), (4)) x (v)) o
                 WHERE o.rn = @n + 1;
                SET @cols = CASE @v
                    WHEN 1 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
                    WHEN 2 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount'
                    WHEN 3 THEN N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
                    ELSE        N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount' END;
                SET @statusPresent = CASE WHEN @v IN (1, 3) THEN 1 ELSE 0 END;
                SET @sql = CONCAT(N'INSERT INTO dbo.CollectionsReceivableStaging (', @cols, N') EXEC ', @proc,
                                  N' @StartDate = @StartDate, @EndDate = @EndDate, @MinAmount = @MinAmount;');
                BEGIN TRY
                    EXEC sys.sp_executesql @sql, N'@StartDate datetime, @EndDate datetime, @MinAmount int',
                         @StartDate = @startDt, @EndDate = @endDt, @MinAmount = @SourceMinAmount;
                    SET @ok = 1;
                END TRY
                BEGIN CATCH
                    SELECT @errNo = ERROR_NUMBER(), @errMsg = ERROR_MESSAGE();
                    DELETE FROM dbo.CollectionsReceivableStaging;
                    IF @errNo <> 213 BREAK;   -- only a column-count/shape mismatch tries the next variant
                    SET @n += 1;
                END CATCH;
            END;

            IF @ok = 1
            BEGIN
                EXEC dbo.usp_Collections_PublishReceivablesStaging
                     @RunId = @RunId, @CompanyId = @c, @CoverageFromDate = @SourceFromDate, @CoverageThroughDate = @SourceThroughDate,
                     @SourceMinAmount = @SourceMinAmount, @ShapeVariant = @v, @StatusColumnPresent = @statusPresent,
                     @MaxRawRows = @MaxRawRows, @MaxShrinkPercent = @MaxShrinkPercent,
                     @ShrinkGuardMinRows = @ShrinkGuardMinRows, @AllowLargeShrink = @AllowLargeShrink;
            END
            ELSE
            BEGIN
                IF @errNo = 213 SET @errMsg = N'The PACT procedure result does not match any known column layout (see V005 header). ' + ISNULL(@errMsg, N'');
                EXEC dbo.usp_Collections_RecordReceivablesFailure @RunId, @c, @errNo, @errMsg;
            END;

            FETCH NEXT FROM company_cursor INTO @c;
        END;
        CLOSE company_cursor; DEALLOCATE company_cursor;

        DECLARE @ok_n int, @fail_n int;
        SELECT @ok_n = SUM(CASE WHEN Status = 'Succeeded' THEN 1 ELSE 0 END), @fail_n = SUM(CASE WHEN Status <> 'Succeeded' THEN 1 ELSE 0 END)
          FROM dbo.CollectionsReceivableRunCompany WHERE RunId = @RunId;
        UPDATE dbo.CollectionsReceivableRun
           SET Status = CASE WHEN ISNULL(@fail_n, 0) = 0 THEN 'Succeeded' WHEN ISNULL(@ok_n, 0) = 0 THEN 'Failed' ELSE 'PartialFailure' END,
               FinishedUtc = SYSUTCDATETIME()
         WHERE RunId = @RunId;
    END TRY
    BEGIN CATCH
        DECLARE @fatal nvarchar(1000) = LEFT(ERROR_MESSAGE(), 1000);
        IF CURSOR_STATUS('local', 'company_cursor') >= 0 CLOSE company_cursor;
        IF CURSOR_STATUS('local', 'company_cursor') >= -1 DEALLOCATE company_cursor;
        UPDATE dbo.CollectionsReceivableRun SET Status = 'Failed', FinishedUtc = SYSUTCDATETIME(), Message = @fatal WHERE RunId = @RunId;
        EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';
        THROW;
    END CATCH;

    EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';

    SELECT RunId, Status AS RunStatus, Message FROM dbo.CollectionsReceivableRun WHERE RunId = @RunId;
    SELECT CompanyId, Status, RawRows, PublishedRows, ExcludedZeroRows, ExcludedInvalidUnitRows, ExcludedInvalidIdentityRows, ErrorNumber, ErrorMessage
      FROM dbo.CollectionsReceivableRunCompany WHERE RunId = @RunId ORDER BY CompanyId;
END;
GO
