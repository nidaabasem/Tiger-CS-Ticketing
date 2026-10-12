/*
  R002 - Refresh ONE company's receivables from PACT into the local snapshot, and close attempts whose session died.

    EXEC dbo.usp_CollectionsRcv_Refresh @CompanyId = 4,  @TriggerSource = N'Manual';
    EXEC dbo.usp_CollectionsRcv_Refresh @CompanyId = 32, @TriggerSource = N'Manual';

  Source (confirmed): [10.10.10.94].[PACTRPT].[dbo].[p4AccountReceivables] (Dubai) / [p32AccountReceivables] (Sharjah).  Edit @LinkedServer /
  @RemoteDatabase below if the topology changes.  Requirements: linked server with RPC Out and Data Access enabled, a login mapping for the TigerCsTicketing
  login, EXECUTE on both procedures.  Nothing in the linked server, MSDTC or the server configuration is changed or required to change by this script.

  RESULT LAYOUT (fixed per company, verified on the live procedures; no guessing, no retrying with another layout):
      company 4   layout 1  CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status   (13)
      company 32  layout 3  CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status               (12)
  INSERT ... EXEC maps by position.  @Layout overrides the company default (tools only).  A mismatch fails the attempt with its real error message.

  WHAT IS STORED: only instalments with a remaining Amount > 0 (Amount is the REMAINING unpaid amount; PACT returns neither the original nor the paid amount),
  for valid units (UnitID > 0, unit code not blank / 0 / containing '*', customer id present).  Excluded rows are counted on the run (and their amount for
  invalid / cancelled units), never silently dropped.  Dates are stored for the whole window 2000-2099; the pages classify against today (Asia/Dubai).
  @SourceMinAmount stays 0: it is an int filter inside PACT (Amount >= @MinAmount) and a higher value would drop small balances.

  ISOLATION: one application lock per company ('Collections.Rcv.<company>', session-owned).  A Running row can only be alive while that lock is held, so
  "is it still running?" is answered by trying to take the lock (usp_CollectionsRcv_ReapDeadRuns) - never by the age of the row, and without VIEW SERVER STATE.
  The fetch lands in a session-local temp table; the other company, the pages and the previous published run are never touched until the publish transaction.
  A client timeout / cancellation / killed process aborts this batch WITHOUT running the CATCH block; the application then closes the run through
  usp_CollectionsRcv_ReapDeadRuns once the connection is gone, and the next attempt closes whatever is left, under the lock, before it starts.

  Always returns ONE row: RunId, CompanyId, Status (Succeeded | Failed | AlreadyRunning), RawRows, PublishedRows, ExcludedSettled/Invalid/CancelledRows, ErrorNumber, ErrorMessage, FetchMs, PublishMs.
*/
-- The client gave up on an attempt (command timeout, cancellation) but its SQL session may still be inside the PACT call, holding the company's lock: record the reason on the
-- attempt now, so that whoever closes it later (usp_CollectionsRcv_ReapDeadRuns, once the session has ended) reports the real reason and not a generic one.
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_NoteClientAbort
    @RunId uniqueidentifier, @ErrorNumber int, @Message nvarchar(1000)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.CollectionsRcvRun SET ErrorNumber = @ErrorNumber, ErrorMessage = LEFT(@Message, 1000) WHERE RunId = @RunId AND Status = 'Running';
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_ReapDeadRuns
    @CompanyId   int           = NULL,        -- NULL = both companies
    @ErrorNumber int           = NULL,
    @Message     nvarchar(1000) = NULL,       -- reason recorded on the dead attempt (e.g. the client timeout); default text otherwise
    @Status      varchar(12)   = 'Abandoned'  -- Abandoned | Failed
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50001, N'Company must be 4, 32 or NULL.', 1;
    IF @Status NOT IN ('Abandoned', 'Failed') THROW 50002, N'Status must be Abandoned or Failed.', 1;
    DECLARE @c int, @lock int, @res nvarchar(100), @msg nvarchar(1000), @no int;
    DECLARE company_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT s.CompanyId FROM dbo.CollectionsRcvCompany s
         WHERE (@CompanyId IS NULL OR s.CompanyId = @CompanyId)
           AND (s.LastStatus = 'Running' OR EXISTS (SELECT 1 FROM dbo.CollectionsRcvRun r WHERE r.CompanyId = s.CompanyId AND r.Status = 'Running'));
    OPEN company_cursor;
    FETCH NEXT FROM company_cursor INTO @c;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @res = CONCAT(N'Collections.Rcv.', @c);
        BEGIN TRANSACTION;
        EXEC @lock = sys.sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
        IF @lock >= 0
        BEGIN
            -- Nobody holds the company's lock: no refresh of this company is executing, so every row still 'Running' belongs to a session that is gone.
            -- A reason the client noted on the attempt (usp_CollectionsRcv_NoteClientAbort) wins over the generic one.
            SELECT TOP (1) @msg = ErrorMessage, @no = ErrorNumber FROM dbo.CollectionsRcvRun WHERE CompanyId = @c AND Status = 'Running' AND ErrorMessage IS NOT NULL ORDER BY StartedUtc DESC;
            IF @msg IS NULL
                SELECT @msg = ISNULL(@Message, N'The refresh ended without a result (client timeout, cancellation or application restart).'), @no = @ErrorNumber;
            UPDATE dbo.CollectionsRcvRun SET Status = @Status, FinishedUtc = SYSUTCDATETIME(), ErrorNumber = ISNULL(ErrorNumber, @no), ErrorMessage = ISNULL(ErrorMessage, @msg)
             WHERE CompanyId = @c AND Status = 'Running';
            UPDATE dbo.CollectionsRcvCompany
               SET LastStatus = @Status, LastFinishedUtc = SYSUTCDATETIME(), LastErrorNumber = @no, LastError = @msg, ConsecutiveFailures = ConsecutiveFailures + 1
             WHERE CompanyId = @c AND LastStatus = 'Running';
            SELECT @msg = NULL, @no = NULL;
        END;
        COMMIT TRANSACTION;
        FETCH NEXT FROM company_cursor INTO @c;
    END;
    CLOSE company_cursor; DEALLOCATE company_cursor;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_Refresh
    @CompanyId          int,
    @TriggerSource      nvarchar(50)  = N'Manual',
    @RunId              uniqueidentifier = NULL,       -- caller-supplied so the caller can find the attempt if this call never returns
    @SourceFromDate     date          = '20000101',
    @SourceThroughDate  date          = '20991231',
    @SourceMinAmount    int           = 0,
    @MaxRawRows         int           = 2000000,
    @MaxShrinkPercent   int           = 60,            -- a result that would shrink the published rows by more than this is refused (previous run kept)
    @ShrinkGuardMinRows int           = 200,
    @AllowLargeShrink   bit           = 0,
    @Layout             tinyint       = NULL,          -- NULL = the company's verified layout (4 -> 1, 32 -> 3)
    @LockWaitMs         int           = 2000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    SET REMOTE_PROC_TRANSACTIONS OFF;

    DECLARE @LinkedServer sysname = N'10.10.10.94', @RemoteDatabase sysname = N'PACTRPT';

    IF @CompanyId NOT IN (4, 32) THROW 50001, N'Company must be 4 or 32.', 1;
    IF @SourceFromDate > @SourceThroughDate THROW 50002, N'@SourceFromDate must not be after @SourceThroughDate.', 1;
    SET @Layout = ISNULL(@Layout, CASE WHEN @CompanyId = 32 THEN 3 ELSE 1 END);
    IF @Layout NOT IN (1, 3) THROW 50003, N'@Layout must be 1 or 3.', 1;
    SET @RunId = ISNULL(@RunId, NEWID());

    DECLARE @res nvarchar(100) = CONCAT(N'Collections.Rcv.', @CompanyId), @lock int;
    EXEC @lock = sys.sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @LockWaitMs;
    IF @lock < 0
    BEGIN
        SELECT CAST(NULL AS uniqueidentifier) AS RunId, @CompanyId AS CompanyId, 'AlreadyRunning' AS Status, CAST(NULL AS int) AS RawRows, CAST(NULL AS int) AS PublishedRows,
               CAST(NULL AS int) AS ExcludedSettledRows, CAST(NULL AS int) AS ExcludedInvalidRows, CAST(NULL AS int) AS ExcludedCancelledRows,
               CAST(NULL AS int) AS ErrorNumber, N'Another refresh of this company is already running; this call did nothing.' AS ErrorMessage, CAST(NULL AS int) AS FetchMs, CAST(NULL AS int) AS PublishMs;
        RETURN;
    END;

    DECLARE @proc nvarchar(400) = CONCAT(QUOTENAME(@LinkedServer), N'.', QUOTENAME(@RemoteDatabase), N'.', QUOTENAME(N'dbo'), N'.', QUOTENAME(CONCAT(N'p', @CompanyId, N'AccountReceivables')));
    DECLARE @t0 datetime2(3), @fetchMs int, @publishMs int, @raw int = 0, @published int = 0, @settled int = 0, @invalid int = 0, @cancelled int = 0, @foreign int,
            @invalidAmt decimal(19,4) = 0, @cancelledAmt decimal(19,4) = 0, @prev int, @errNo int, @errMsg nvarchar(1000), @status varchar(12) = 'Failed',
            @sql nvarchar(max), @cols nvarchar(max), @startDt datetime, @endDt datetime;

    BEGIN TRY
        -- We hold the company's lock: any 'Running' row of this company belongs to a session that is gone.
        UPDATE dbo.CollectionsRcvRun SET Status = 'Abandoned', FinishedUtc = SYSUTCDATETIME(), ErrorMessage = N'Superseded: the attempt ended without a result (timeout, cancellation or restart).'
         WHERE CompanyId = @CompanyId AND Status = 'Running';
        INSERT dbo.CollectionsRcvRun (RunId, CompanyId, TriggerSource, StartedUtc, Status, SourceFromDate, SourceThroughDate, SourceMinAmount, Layout)
        VALUES (@RunId, @CompanyId, @TriggerSource, SYSUTCDATETIME(), 'Running', @SourceFromDate, @SourceThroughDate, @SourceMinAmount, @Layout);
        UPDATE dbo.CollectionsRcvCompany
           SET LastRunId = @RunId, LastAttemptUtc = SYSUTCDATETIME(), LastFinishedUtc = NULL, LastStatus = 'Running', LastErrorNumber = NULL, LastError = NULL
         WHERE CompanyId = @CompanyId;

        CREATE TABLE #src
        (
            CompanyID int NULL, ProjectCode nvarchar(200) NULL, UnitCode nvarchar(200) NULL, TenantID nvarchar(100) NULL, FullName nvarchar(400) NULL,
            Mobile nvarchar(100) NULL, Email nvarchar(400) NULL, UnitID bigint NULL, VoucherNumber nvarchar(100) NULL, ChequeNumber nvarchar(100) NULL,
            DueDate datetime NULL, Amount decimal(19,4) NULL, Status nvarchar(50) NULL
        );
        SET @cols = CASE @Layout
            WHEN 1 THEN N'CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status'
            ELSE        N'CompanyID, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status' END;
        SELECT @startDt = CAST(@SourceFromDate AS datetime), @endDt = DATEADD(MILLISECOND, -3, DATEADD(DAY, 1, CAST(@SourceThroughDate AS datetime)));   -- 23:59:59.997
        SET @sql = CONCAT(N'INSERT INTO #src (', @cols, N') EXEC ', @proc, N' @StartDate = @StartDate, @EndDate = @EndDate, @MinAmount = @MinAmount;');

        SET @t0 = SYSUTCDATETIME();
        EXEC sys.sp_executesql @sql, N'@StartDate datetime, @EndDate datetime, @MinAmount int', @StartDate = @startDt, @EndDate = @endDt, @MinAmount = @SourceMinAmount;
        SET @fetchMs = DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME());

        SELECT @raw = COUNT(*), @foreign = SUM(CASE WHEN CompanyID IS NULL OR CompanyID <> @CompanyId THEN 1 ELSE 0 END) FROM #src;
        IF @raw > @MaxRawRows THROW 50010, N'PACT returned more rows than the configured limit; nothing was published.', 1;
        IF ISNULL(@foreign, 0) > 0 THROW 50011, N'PACT returned rows that do not belong to the requested company; nothing was published.', 1;

        SELECT @settled = SUM(CASE WHEN Amount IS NULL OR Amount <= 0 THEN 1 ELSE 0 END) FROM #src;
        -- Valid = unpaid, with a customer, a due date, a real unit id and a real unit code (not blank / 0), and not a cancelled apartment ('*').
        SELECT @cancelled = COUNT(*), @cancelledAmt = ISNULL(SUM(Amount), 0) FROM #src WHERE Amount > 0 AND UnitCode LIKE N'%*%';
        SELECT @invalid = COUNT(*), @invalidAmt = ISNULL(SUM(Amount), 0) FROM #src
         WHERE Amount > 0 AND ISNULL(UnitCode, N'') NOT LIKE N'%*%'
           AND (UnitID IS NULL OR UnitID <= 0 OR LTRIM(RTRIM(ISNULL(UnitCode, N''))) IN (N'', N'0') OR LTRIM(RTRIM(ISNULL(TenantID, N''))) = N'' OR DueDate IS NULL);
        SET @published = @raw - ISNULL(@settled, 0) - @cancelled - @invalid;

        SELECT @prev = PublishedRows FROM dbo.CollectionsRcvCompany WHERE CompanyId = @CompanyId AND CurrentRunId IS NOT NULL;
        IF @AllowLargeShrink = 0 AND @prev >= @ShrinkGuardMinRows AND @published < @prev * (100 - @MaxShrinkPercent) / 100
            THROW 50012, N'The new result is much smaller than the published snapshot; it was not published (previous snapshot kept). Re-run with @AllowLargeShrink = 1 if the drop is real.', 1;

        SET @t0 = SYSUTCDATETIME();
        BEGIN TRANSACTION;
            INSERT dbo.CollectionsRcvLine (RunId, CompanyId, TenantId, UnitId, UnitCode, TowerNumber, ProjectCode, FullName, Mobile, Email, VoucherNumber, ChequeNumber, DueDate, Amount, SourceStatus)
            SELECT @RunId, @CompanyId, LTRIM(RTRIM(TenantID)), UnitID, LTRIM(RTRIM(UnitCode)), dbo.fn_CollectionsTowerNumber(UnitCode), ISNULL(ProjectCode, N''), ISNULL(FullName, N''),
                   ISNULL(Mobile, N''), ISNULL(Email, N''), ISNULL(VoucherNumber, N''), ISNULL(ChequeNumber, N''), CAST(DueDate AS date), Amount, Status
              FROM #src
             WHERE Amount > 0 AND UnitCode NOT LIKE N'%*%' AND UnitID > 0 AND LTRIM(RTRIM(ISNULL(UnitCode, N''))) NOT IN (N'', N'0') AND LTRIM(RTRIM(ISNULL(TenantID, N''))) <> N'' AND DueDate IS NOT NULL;
            IF @@ROWCOUNT <> @published THROW 50013, N'The number of stored rows does not match the validated count; nothing was published.', 1;
            UPDATE dbo.CollectionsRcvCompany
               SET CurrentRunId = @RunId, LastSuccessUtc = SYSUTCDATETIME(), PublishedRows = @published, LastStatus = 'Succeeded', LastFinishedUtc = SYSUTCDATETIME(),
                   LastErrorNumber = NULL, LastError = NULL, ConsecutiveFailures = 0
             WHERE CompanyId = @CompanyId;
            SET @publishMs = DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME());
            UPDATE dbo.CollectionsRcvRun
               SET Status = 'Succeeded', FinishedUtc = SYSUTCDATETIME(), RawRows = @raw, PublishedRows = @published, ExcludedSettledRows = ISNULL(@settled, 0),
                   ExcludedInvalidRows = @invalid, ExcludedInvalidAmount = @invalidAmt, ExcludedCancelledRows = @cancelled, ExcludedCancelledAmount = @cancelledAmt,
                   FetchMs = @fetchMs, PublishMs = @publishMs
             WHERE RunId = @RunId;
        COMMIT TRANSACTION;
        SET @status = 'Succeeded';

        -- Superseded published lines of this company (the pointer already moved; readers never see them).
        DECLARE @del int = 1;
        WHILE @del > 0
        BEGIN
            DELETE TOP (50000) FROM dbo.CollectionsRcvLine WHERE CompanyId = @CompanyId AND RunId <> @RunId;
            SET @del = @@ROWCOUNT;
        END;
        DELETE FROM dbo.CollectionsRcvRun WHERE CompanyId = @CompanyId AND StartedUtc < DATEADD(DAY, -60, SYSUTCDATETIME()) AND RunId <> @RunId;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SELECT @errNo = ERROR_NUMBER(), @errMsg = LEFT(ERROR_MESSAGE(), 1000);
        BEGIN TRY
            UPDATE dbo.CollectionsRcvRun
               SET Status = 'Failed', FinishedUtc = SYSUTCDATETIME(), RawRows = NULLIF(@raw, 0), ErrorNumber = @errNo, ErrorMessage = @errMsg, FetchMs = @fetchMs
             WHERE RunId = @RunId;
            UPDATE dbo.CollectionsRcvCompany
               SET LastStatus = 'Failed', LastFinishedUtc = SYSUTCDATETIME(), LastErrorNumber = @errNo, LastError = @errMsg, ConsecutiveFailures = ConsecutiveFailures + 1
             WHERE CompanyId = @CompanyId AND LastRunId = @RunId;
        END TRY
        BEGIN CATCH
        END CATCH;
        SET @status = 'Failed';
    END CATCH;

    EXEC sys.sp_releaseapplock @Resource = @res, @LockOwner = N'Session';

    SELECT @RunId AS RunId, @CompanyId AS CompanyId, @status AS Status, NULLIF(@raw, 0) AS RawRows, CASE WHEN @status = 'Succeeded' THEN @published END AS PublishedRows,
           CASE WHEN @status = 'Succeeded' THEN ISNULL(@settled, 0) END AS ExcludedSettledRows, CASE WHEN @status = 'Succeeded' THEN @invalid END AS ExcludedInvalidRows,
           CASE WHEN @status = 'Succeeded' THEN @cancelled END AS ExcludedCancelledRows, @errNo AS ErrorNumber, @errMsg AS ErrorMessage, @fetchMs AS FetchMs, CASE WHEN @status = 'Succeeded' THEN @publishMs END AS PublishMs;
END;
GO
