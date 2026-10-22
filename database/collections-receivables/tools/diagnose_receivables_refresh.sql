/*
  READ-ONLY diagnostics of the receivables refresh (TigerCsTicketing).  Changes no data.  Run it when a page shows "Loading data for ..." for long or "Could not load data for ...".

  1  server / database identity and the linked server [10.10.10.94] as configured (provider, rpc out, data access, when it was last modified)
  2  the state of each company (what the pages show) and its last attempt
  3  the last 20 attempts with duration, counts and the real error
  4  whether a refresh of each company is REALLY executing: tries the company's application lock for 0 ms.  If it is granted, nobody is refreshing that company, so a 'Running' row
     is an orphan (it is closed by the next status read or refresh); if it is not granted, a session is inside the refresh - the age of the row proves nothing either way.
  5  what is published (rows per company, newest/oldest due date, total) - the figures the pages are built from
  1b the timeouts that cap a linked-server call independently of the application's 1800 s command timeout: the linked server's own query_timeout / connect_timeout and the
     server-wide "remote query timeout (s)" (default 600).  A refresh that fails with 'Query timeout expired' after ~600 s (see section 3b) is capped HERE, not by the application.
  3b failures grouped per company and error (count, shortest / longest run in seconds): a cluster of the same error at the same duration (600, 1800) names the limit that was hit
  6  the sessions that run the refresh procedure (needs VIEW SERVER STATE; skipped quietly when the login does not have it)
*/
SET NOCOUNT ON;
SELECT @@SERVERNAME AS ServerName, DB_NAME() AS DatabaseName, SYSUTCDATETIME() AS NowUtc, @@VERSION AS Version;

SELECT s.name AS LinkedServer, s.product, s.provider, s.data_source, s.is_rpc_out_enabled AS RpcOut, s.is_data_access_enabled AS DataAccess, s.is_remote_proc_transaction_promotion_enabled AS PromotionOfDistributedTransactions, s.modify_date
  FROM sys.servers s WHERE s.name = N'10.10.10.94';

-- 1b. timeouts (0 on a linked server = "use the server-wide remote query timeout")
SELECT s.name AS LinkedServer, s.query_timeout AS LinkedServerQueryTimeoutSec, s.connect_timeout AS LinkedServerConnectTimeoutSec,
       (SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'remote query timeout (s)') AS ServerRemoteQueryTimeoutSec
  FROM sys.servers s WHERE s.name = N'10.10.10.94';

SELECT CompanyId, CASE WHEN CurrentRunId IS NULL THEN 'NOT LOADED' ELSE 'published' END AS Snapshot, PublishedRows, LastSuccessUtc,
       DATEDIFF(MINUTE, LastSuccessUtc, SYSUTCDATETIME()) AS AgeMinutes, LastStatus, LastAttemptUtc, LastFinishedUtc, LastErrorNumber, LastError, ConsecutiveFailures
  FROM dbo.CollectionsRcvCompany ORDER BY CompanyId;

SELECT TOP (20) CompanyId, TriggerSource, StartedUtc, FinishedUtc, DATEDIFF(SECOND, StartedUtc, ISNULL(FinishedUtc, SYSUTCDATETIME())) AS Seconds, Status, Layout,
       RawRows, PublishedRows, ExcludedSettledRows, ExcludedInvalidRows, ExcludedCancelledRows, FetchMs, PublishMs, ErrorNumber, ErrorMessage
  FROM dbo.CollectionsRcvRun ORDER BY StartedUtc DESC;

-- 3b. what keeps failing, per company (the last 60 days kept by the refresh procedure)
SELECT CompanyId, Status, ErrorNumber, LEFT(ErrorMessage, 160) AS ErrorMessageStart, COUNT(*) AS Attempts,
       MIN(DATEDIFF(SECOND, StartedUtc, FinishedUtc)) AS ShortestSeconds, MAX(DATEDIFF(SECOND, StartedUtc, FinishedUtc)) AS LongestSeconds,
       MAX(StartedUtc) AS LastStartedUtc, SUM(CASE WHEN FetchMs IS NULL THEN 1 ELSE 0 END) AS FailedBeforeTheFetchFinished
  FROM dbo.CollectionsRcvRun WHERE Status <> 'Succeeded' AND FinishedUtc IS NOT NULL
 GROUP BY CompanyId, Status, ErrorNumber, LEFT(ErrorMessage, 160) ORDER BY CompanyId, Attempts DESC;

-- 4. Is a refresh really executing?  (the probe takes the lock for the length of this transaction and releases it; nothing is written)
DECLARE @c int, @lock int, @res nvarchar(100);
CREATE TABLE #probe (CompanyId int, RunningRows int, LockHeldBySomeone bit, Reading nvarchar(200));
DECLARE company_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT CompanyId FROM dbo.CollectionsRcvCompany ORDER BY CompanyId;
OPEN company_cursor; FETCH NEXT FROM company_cursor INTO @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @res = CONCAT(N'Collections.Rcv.', @c);
    BEGIN TRANSACTION;
    EXEC @lock = sys.sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
    ROLLBACK TRANSACTION;
    INSERT #probe SELECT @c, (SELECT COUNT(*) FROM dbo.CollectionsRcvRun WHERE CompanyId = @c AND Status = 'Running'), CASE WHEN @lock < 0 THEN 1 ELSE 0 END,
        CASE WHEN @lock < 0 THEN N'a session holds the lock: a refresh of this company is executing (Loading is true)'
             WHEN EXISTS (SELECT 1 FROM dbo.CollectionsRcvRun WHERE CompanyId = @c AND Status = 'Running') THEN N'NO session holds the lock but a row says Running: an orphan, closed by the next status read'
             ELSE N'idle' END;
    FETCH NEXT FROM company_cursor INTO @c;
END;
CLOSE company_cursor; DEALLOCATE company_cursor;
SELECT * FROM #probe ORDER BY CompanyId;

SELECT l.CompanyId, COUNT_BIG(*) AS PublishedPayments, COUNT(DISTINCT CONCAT(TenantId, N'|', UnitId, N'|', UnitCode)) AS Apartments, SUM(Amount) AS RemainingTotal,
       MIN(DueDate) AS OldestDue, MAX(DueDate) AS NewestDue, SUM(CASE WHEN DueDate > CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date) THEN 1 ELSE 0 END) AS FuturePaymentsStored
  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId = l.RunId GROUP BY l.CompanyId ORDER BY l.CompanyId;

BEGIN TRY
    SELECT s.session_id, s.status, s.login_name, s.host_name, s.program_name, DATEDIFF(SECOND, s.last_request_start_time, GETDATE()) AS RunningSeconds, r.wait_type, r.wait_time, LEFT(t.text, 100) AS Statement
      FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
     WHERE t.text LIKE N'%usp_CollectionsRcv_Refresh%' AND s.session_id <> @@SPID;
END TRY
BEGIN CATCH
    PRINT N'Section 6 skipped: ' + ERROR_MESSAGE();
END CATCH;
