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
  * PAYMENT STATUS: the deployed procedures return only the remaining Amount and Status (Paid / Installment) - the original and allocated amounts are
    computed inside them but not returned, so Unpaid vs Partially paid cannot be told apart from their output. With @ProcedureSuffix = N'V2' (the
    reviewed companion draft in docs/Collections/pact-sql, deployed separately) layouts 5-6 also carry PlanAmount / AllocatedAmount and the publish step
    stores a verified status. Note the companion changes the allocation arithmetic (it fixes defects D1-D5 of the source review): reconcile it first.
  * Overlap protection: a session-scoped application lock. A second concurrent call returns RunStatus = 'AlreadyRunning'.
  * TERMINAL STATES: a client timeout / cancellation / application restart aborts this batch WITHOUT running the CATCH block below. The caller
    therefore passes its own @RunId and, when the call did not return, asks dbo.usp_Collections_CloseReceivablesRun (V004) to close that run.
    Whatever is left over (hard process kill) is closed here, under the lock, at the start of the next run: Run AND RunCompany rows, and the
    company's last-attempt state. A Running row can only be live while this lock is held.
  * COVERAGE: @SourceFromDate/@SourceThroughDate is the due-date window requested from PACT. With @ExtendCoverage = 1 (default) the window used
    for each company is the UNION of the request and the coverage already published for that company, so the scheduled refresh keeps a range
    that was loaded on demand (the application's "Load missing data" action calls this procedure with the missing dates). Pass 0 to replace the
    coverage with exactly the requested window. Pages compare the requested From/To with the stored coverage and show any gap; they never
    treat an uncovered range as zero receivables.
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
    @AllowLargeShrink   bit          = 0,
    @ExtendCoverage     bit          = 1,          -- 1 = never shrink a company's stored coverage (see below)
    @ProcedureSuffix    nvarchar(16) = N'',        -- '' = the deployed p4/p32AccountReceivables; e.g. 'V2' = a companion procedure that also returns original + allocated amounts
    @RetainPaid         bit          = 1,          -- keep fully paid instalments (needed for the "Fully paid" and "All" views)
    @StrictIdentity     bit          = 0,          -- companion procedures only: 1 = fail instead of guessing an ambiguous voucher/unit
    @RunId              uniqueidentifier = NULL        -- caller-supplied id so the caller can close the run if this call never returns; NULL = generated here
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    SET REMOTE_PROC_TRANSACTIONS OFF;

    DECLARE @LinkedServer sysname = N'10.10.10.94', @RemoteDatabase sysname = N'PACTRPT';

    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50001, N'Company must be 4, 32 or NULL.', 1;
    IF @SourceFromDate > @SourceThroughDate THROW 50002, N'@SourceFromDate must not be after @SourceThroughDate.', 1;
    IF @ProcedureSuffix LIKE N'%[^A-Za-z0-9]%' THROW 50003, N'@ProcedureSuffix must be letters and digits only.', 1;

    DECLARE @lock int;
    EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive',
                                   @LockOwner = N'Session', @LockTimeout = 0;
    IF @lock < 0
    BEGIN
        SELECT CAST(NULL AS uniqueidentifier) AS RunId, 'AlreadyRunning' AS RunStatus,
               N'Another refresh is already running; this call did nothing.' AS Message;
        SELECT CAST(NULL AS int) AS CompanyId, CAST(NULL AS varchar(20)) AS Status, CAST(NULL AS int) AS RawRows, CAST(NULL AS int) AS PublishedRows,
               CAST(NULL AS int) AS ExcludedZeroRows, CAST(NULL AS int) AS ExcludedInvalidUnitRows, CAST(NULL AS int) AS ExcludedInvalidIdentityRows,
               CAST(NULL AS int) AS ErrorNumber, CAST(NULL AS nvarchar(1000)) AS ErrorMessage,
               CAST(NULL AS int) AS FetchMs, CAST(NULL AS int) AS ValidateMs, CAST(NULL AS int) AS PublishMs WHERE 1 = 0;
        RETURN;
    END;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    SET @RunId = ISNULL(@RunId, NEWID());
    DECLARE @startDt datetime, @endDt datetime;

    BEGIN TRY
        -- We hold the lock, so any 'Running' row (run OR company) belongs to an earlier attempt whose session died: give it a terminal state
        -- and record it as that company's last attempt (the earlier published snapshot stays untouched).
        EXEC dbo.usp_Collections_CloseReceivablesRun @RunId = NULL, @ErrorNumber = -1,
             @Message = N'The refresh was interrupted before it finished (timeout, cancellation or application restart); superseded by a later refresh.',
             @RunStatus = 'Abandoned', @RequireIdle = 0;
        INSERT dbo.CollectionsReceivableRun (RunId, TriggerSource, RequestedCompanyId, StartedUtc, Status)
        VALUES (@RunId, @TriggerSource, @CompanyId, @now, 'Running');

        DECLARE @c int, @pref tinyint, @n int, @v tinyint, @ok bit, @sql nvarchar(max), @cols nvarchar(max), @proc nvarchar(400),
                @errNo int, @errMsg nvarchar(4000), @statusPresent bit, @cFrom date, @cThrough date, @tFetch datetime2(3), @fetchMs int, @nVariants int, @breakdown bit;

        DECLARE company_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT v.CompanyId FROM (VALUES (4), (32)) v (CompanyId) WHERE @CompanyId IS NULL OR v.CompanyId = @CompanyId ORDER BY v.CompanyId;
        OPEN company_cursor;
        FETCH NEXT FROM company_cursor INTO @c;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            INSERT dbo.CollectionsReceivableRunCompany (RunId, CompanyId, StartedUtc, Status) VALUES (@RunId, @c, SYSUTCDATETIME(), 'Running');
            DELETE FROM dbo.CollectionsReceivableStaging;

            SET @proc = CONCAT(QUOTENAME(@LinkedServer), N'.', QUOTENAME(@RemoteDatabase), N'.', QUOTENAME(N'dbo'), N'.',
                               QUOTENAME(CONCAT(N'p', @c, N'AccountReceivables', @ProcedureSuffix)));
            SELECT @pref = ISNULL(ShapeVariant, 1),
                   @cFrom = CASE WHEN @ExtendCoverage = 1 AND CoverageFromDate    IS NOT NULL AND CoverageFromDate    < @SourceFromDate    THEN CoverageFromDate    ELSE @SourceFromDate    END,
                   @cThrough = CASE WHEN @ExtendCoverage = 1 AND CoverageThroughDate IS NOT NULL AND CoverageThroughDate > @SourceThroughDate THEN CoverageThroughDate ELSE @SourceThroughDate END
              FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @c;
            SELECT @startDt = CAST(@cFrom AS datetime),
                   @endDt   = DATEADD(MILLISECOND, -3, DATEADD(DAY, 1, CAST(@cThrough AS datetime)));   -- 23:59:59.997
            SELECT @n = 0, @ok = 0, @errNo = NULL, @errMsg = NULL, @v = NULL;

            SET @tFetch = SYSUTCDATETIME();
            SET @nVariants = CASE WHEN @ProcedureSuffix = N'' THEN 4 ELSE 2 END;
            WHILE @ok = 0 AND @n < @nVariants
            BEGIN
                -- Deployed procedures: layouts 1-4. Companion (suffix) procedures: layouts 5-6. The layout that worked last time is tried first.
                SELECT @v = o.v
                  FROM (SELECT x.v, ROW_NUMBER() OVER (ORDER BY CASE WHEN x.v = @pref THEN 0 ELSE 1 END, x.v) AS rn
                          FROM (VALUES (1), (2), (3), (4), (5), (6)) x (v)
                         WHERE (@ProcedureSuffix = N'' AND x.v <= 4) OR (@ProcedureSuffix <> N'' AND x.v >= 5)) o
                 WHERE o.rn = @n + 1;
                SET @cols = CASE @v
                    WHEN 1 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
                    WHEN 2 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount'
                    WHEN 3 THEN N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
                    WHEN 4 THEN N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount'
                    WHEN 5 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status, PaymentTermAccountId, PlanAmount, AllocatedAmount'
                    ELSE        N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status, PaymentTermAccountId, PlanAmount, AllocatedAmount' END;
                SET @statusPresent = CASE WHEN @v IN (1, 3, 5, 6) THEN 1 ELSE 0 END;
                SET @breakdown = CASE WHEN @v IN (5, 6) THEN 1 ELSE 0 END;
                SET @sql = CONCAT(N'INSERT INTO dbo.CollectionsReceivableStaging (', @cols, N') EXEC ', @proc,
                                  N' @StartDate = @StartDate, @EndDate = @EndDate, @MinAmount = @MinAmount',
                                  CASE WHEN @ProcedureSuffix = N'' THEN N';' ELSE N', @IncludeSettled = @IncludeSettled, @StrictIdentity = @StrictIdentity;' END);
                BEGIN TRY
                    EXEC sys.sp_executesql @sql, N'@StartDate datetime, @EndDate datetime, @MinAmount int, @IncludeSettled bit, @StrictIdentity bit',
                         @StartDate = @startDt, @EndDate = @endDt, @MinAmount = @SourceMinAmount, @IncludeSettled = @RetainPaid, @StrictIdentity = @StrictIdentity;
                    SET @ok = 1;
                END TRY
                BEGIN CATCH
                    SELECT @errNo = ERROR_NUMBER(), @errMsg = ERROR_MESSAGE();
                    DELETE FROM dbo.CollectionsReceivableStaging;
                    IF @errNo <> 213 BREAK;   -- only a column-count/shape mismatch tries the next variant
                    SET @n += 1;
                END CATCH;
            END;

            SET @fetchMs = DATEDIFF(MILLISECOND, @tFetch, SYSUTCDATETIME());
            IF @ok = 1
            BEGIN
                EXEC dbo.usp_Collections_PublishReceivablesStaging
                     @RunId = @RunId, @CompanyId = @c, @CoverageFromDate = @cFrom, @CoverageThroughDate = @cThrough,
                     @SourceMinAmount = @SourceMinAmount, @ShapeVariant = @v, @StatusColumnPresent = @statusPresent,
                     @MaxRawRows = @MaxRawRows, @MaxShrinkPercent = @MaxShrinkPercent,
                     @ShrinkGuardMinRows = @ShrinkGuardMinRows, @AllowLargeShrink = @AllowLargeShrink, @FetchMs = @fetchMs, @RetainPaid = @RetainPaid, @BreakdownPresent = @breakdown;
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
        DECLARE @fatal nvarchar(1000) = LEFT(ERROR_MESSAGE(), 1000), @fatalNo int = ERROR_NUMBER();
        IF CURSOR_STATUS('local', 'company_cursor') >= 0 CLOSE company_cursor;
        IF CURSOR_STATUS('local', 'company_cursor') >= -1 DEALLOCATE company_cursor;
        -- Company rows too: a company that was Running when the failure hit must not stay Running (it would read as "loading" forever).
        BEGIN TRY
            EXEC dbo.usp_Collections_CloseReceivablesRun @RunId = @RunId, @ErrorNumber = @fatalNo, @Message = @fatal, @RunStatus = 'Failed', @RequireIdle = 0;
        END TRY
        BEGIN CATCH
            UPDATE dbo.CollectionsReceivableRun SET Status = 'Failed', FinishedUtc = SYSUTCDATETIME(), Message = @fatal WHERE RunId = @RunId AND Status = 'Running';
        END CATCH;
        EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';
        THROW;
    END CATCH;

    EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';

    SELECT RunId, Status AS RunStatus, Message FROM dbo.CollectionsReceivableRun WHERE RunId = @RunId;
    SELECT CompanyId, Status, RawRows, PublishedRows, ExcludedZeroRows, ExcludedInvalidUnitRows, ExcludedInvalidIdentityRows, ErrorNumber, ErrorMessage,
           FetchMs, ValidateMs, PublishMs
      FROM dbo.CollectionsReceivableRunCompany WHERE RunId = @RunId ORDER BY CompanyId;
END;
GO
