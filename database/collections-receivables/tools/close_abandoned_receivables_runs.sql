/*
  Close ABANDONED receivables refresh rows for ONE company. Default is a DRY RUN: it only lists what it would change.

  Use when CollectionsReceivableRunCompany rows are still 'Running' although no refresh is running (e.g. after an application restart or a
  command timeout). Not needed once the fixed V004/V005/V006 and application are deployed: the next refresh closes such rows by itself.

  Safety checks (all must hold, otherwise nothing is changed):
    1. No refresh is running: the refresh application lock is taken with NO wait. If it cannot be taken, something is live -> stop.
    2. Only rows of @CompanyId with Status = 'Running', FinishedUtc IS NULL and no result recorded (no rows, no error), older than @MinAgeMinutes.
    3. Other companies, finished rows, the published snapshot, coverage and LastSuccessUtc are never touched.
    4. Everything is one transaction; the script ends by listing what is still 'Running' for the company (expected: nothing).
  To clean up exactly the rows seen on 2026-10-09/10 for Sharjah, keep @CompanyId = 32 and run the dry run first; the listed StartedUtc values
  must be 2026-10-09 10:35:58.894, 2026-10-09 19:02:02.646 and 2026-10-10 07:54:45.942 (UTC) and nothing else.
*/
SET NOCOUNT ON;
DECLARE @CompanyId int = 32;
DECLARE @MinAgeMinutes int = 60;       -- longer than the application's command timeout (default 30 min) + margin
DECLARE @Apply bit = 0;                -- 0 = list only; 1 = apply

DECLARE @lock int, @held bit = 0, @now datetime2(3) = SYSUTCDATETIME(), @msg nvarchar(1000) = N'Closed by operator: the refresh was interrupted before it finished (no live refresh at the time).';
EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
IF @lock < 0
BEGIN
    SELECT N'STOP: a refresh holds the lock right now. Nothing was changed.' AS Result;
END
ELSE
BEGIN
    SET @held = 1;
    BEGIN TRY
        DECLARE @targets TABLE (RunId uniqueidentifier NOT NULL PRIMARY KEY, StartedUtc datetime2(3) NOT NULL);
        INSERT @targets (RunId, StartedUtc)
        SELECT rc.RunId, rc.StartedUtc
          FROM dbo.CollectionsReceivableRunCompany rc
         WHERE rc.CompanyId = @CompanyId AND rc.Status = 'Running' AND rc.FinishedUtc IS NULL
           AND rc.RawRows IS NULL AND rc.PublishedRows IS NULL AND rc.ErrorNumber IS NULL
           AND rc.StartedUtc < DATEADD(MINUTE, -@MinAgeMinutes, @now);

        SELECT t.RunId, t.StartedUtc, r.Status AS RunStatus, r.TriggerSource, @CompanyId AS CompanyId,
               CASE WHEN @Apply = 1 THEN N'WILL BE CLOSED NOW' ELSE N'dry run - would be closed' END AS Action
          FROM @targets t JOIN dbo.CollectionsReceivableRun r ON r.RunId = t.RunId ORDER BY t.StartedUtc;

        IF @Apply = 1 AND EXISTS (SELECT 1 FROM @targets)
        BEGIN
            BEGIN TRAN;
            UPDATE rc SET Status = 'Failed', FinishedUtc = @now, ErrorNumber = -1, ErrorMessage = @msg
              FROM dbo.CollectionsReceivableRunCompany rc JOIN @targets t ON t.RunId = rc.RunId
             WHERE rc.CompanyId = @CompanyId AND rc.Status = 'Running';

            -- A run row is closed only when no company of it is still Running.
            UPDATE r SET Status = CASE WHEN EXISTS (SELECT 1 FROM dbo.CollectionsReceivableRunCompany k WHERE k.RunId = r.RunId AND k.Status = 'Succeeded') THEN 'PartialFailure' ELSE 'Abandoned' END,
                         FinishedUtc = @now, Message = @msg
              FROM dbo.CollectionsReceivableRun r JOIN @targets t ON t.RunId = r.RunId
             WHERE r.Status = 'Running' AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsReceivableRunCompany k WHERE k.RunId = r.RunId AND k.Status = 'Running');

            -- The newest closed attempt becomes the last attempt, unless a later attempt is already recorded.
            UPDATE st SET LastAttemptRunId = x.RunId, LastAttemptUtc = @now, LastAttemptStatus = 'Failed', LastErrorNumber = -1, LastError = @msg, ConsecutiveFailures = st.ConsecutiveFailures + 1
              FROM dbo.CollectionsReceivableCompanyState st
              JOIN (SELECT TOP (1) RunId, StartedUtc FROM @targets ORDER BY StartedUtc DESC) x ON 1 = 1
             WHERE st.CompanyId = @CompanyId AND (st.LastAttemptUtc IS NULL OR st.LastAttemptUtc <= x.StartedUtc);
            COMMIT;
        END;

        SELECT rc.RunId, rc.CompanyId, rc.StartedUtc, rc.Status AS StillRunningAfterCleanup
          FROM dbo.CollectionsReceivableRunCompany rc WHERE rc.CompanyId = @CompanyId AND rc.Status = 'Running';
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        SELECT N'ERROR - rolled back, nothing changed' AS Result, ERROR_NUMBER() AS ErrorNumber, ERROR_MESSAGE() AS ErrorMessage;
    END CATCH;
    IF @held = 1 EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';
END;
