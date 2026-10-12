/*
  Receivables refresh - READ-ONLY diagnostics. Run on the SAME server/database the application writes to (TigerCsTicketing).
  Changes nothing, takes no locks, needs no credentials. Sections 5-6 need VIEW SERVER STATE and say so when they are not allowed.

  Answers: which server/database am I looking at; how is the linked server really configured and when did it last change (explains a provider
  that differs from an older recorded error); what is the state of each company; which runs/companies are still marked Running and how old they are;
  does a live session hold the refresh lock right now; is the remote server reachable at all.
*/
SET NOCOUNT ON;

-- 1. Identity: compare with ConnectionStrings:TigerCsDatabase of the API/worker (server + database only, never the password).
SELECT @@SERVERNAME AS ServerName, SERVERPROPERTY('InstanceName') AS InstanceName, DB_NAME() AS DatabaseName,
       CONNECTIONPROPERTY('local_net_address') AS LocalAddress, SYSUTCDATETIME() AS NowUtc, @@VERSION AS Version;

-- 2. Linked server(s) as configured NOW. modify_date vs LastAttemptUtc of section 3: an error recorded before modify_date describes the OLD
--    configuration (e.g. provider MSOLEDBSQL) and says nothing about the current one (SQLNCLI).
SELECT s.server_id, s.name, s.product, s.provider, s.data_source, s.is_data_access_enabled AS DataAccess, s.is_rpc_out_enabled AS RpcOut,
       s.is_remote_proc_transaction_promotion_enabled AS RemoteProcTransactionPromotion, s.connect_timeout AS ConnectTimeoutS, s.query_timeout AS QueryTimeoutS, s.modify_date
  FROM sys.servers s WHERE s.is_linked = 1 AND (s.name = N'10.10.10.94' OR s.data_source = N'10.10.10.94');
SELECT name, value_in_use FROM sys.configurations WHERE name IN (N'remote query timeout (s)', N'remote proc trans');

-- 3. Per-company state: LastAttemptUtc / LastAttemptStatus / LastErrorNumber / SnapshotRowCount are only written when a run FINISHES (success or recorded failure).
SELECT CompanyId, CurrentRunId, LastAttemptRunId, LastAttemptUtc, LastAttemptStatus, LastErrorNumber, LEFT(LastError, 300) AS LastError,
       LastSuccessUtc, ConsecutiveFailures, SnapshotRowCount, CoverageFromDate, CoverageThroughDate
  FROM dbo.CollectionsReceivableCompanyState ORDER BY CompanyId;

-- 4. Runs and companies still marked Running, with age and the verdict of BOTH loading rules for each company:
--    OldRule = the previous usp_Collections_GetCoverage (any Running run started < 3 h ago);
--    NewRule = the current one (started < 40 min ago AND this company's own row of the run not finished).
--    A page that says "Loading data for ..." is explained by exactly the rows with OldRule/NewRule = 1 (ages are in UTC minutes).
SELECT r.RunId, r.TriggerSource, r.RequestedCompanyId, r.StartedUtc, DATEDIFF(MINUTE, r.StartedUtc, SYSUTCDATETIME()) AS RunAgeMinutes, r.FinishedUtc, r.Status AS RunStatus,
       rc.CompanyId, rc.Status AS CompanyStatus, rc.RawRows, rc.PublishedRows, rc.ErrorNumber,
       CAST(CASE WHEN r.Status = 'Running' AND r.StartedUtc > DATEADD(HOUR, -3, SYSUTCDATETIME()) THEN 1 ELSE 0 END AS bit) AS OldRule,
       CAST(CASE WHEN r.Status = 'Running' AND r.StartedUtc > DATEADD(MINUTE, -40, SYSUTCDATETIME()) AND (rc.RunId IS NULL OR rc.Status = 'Running') THEN 1 ELSE 0 END AS bit) AS NewRule
  FROM dbo.CollectionsReceivableRun r
  LEFT JOIN dbo.CollectionsReceivableRunCompany rc ON rc.RunId = r.RunId
 WHERE r.Status = 'Running' OR rc.Status = 'Running' OR r.StartedUtc > DATEADD(DAY, -2, SYSUTCDATETIME())
 ORDER BY r.StartedUtc DESC, rc.CompanyId;

-- 5. Does a live session hold the refresh lock right now? (a Running row is only live while this returns a row)
BEGIN TRY
    SELECT l.request_session_id AS SessionId, l.request_mode, s.login_name, s.host_name, s.program_name, s.status, s.last_request_start_time, s.last_request_end_time
      FROM sys.dm_tran_locks l LEFT JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id
     WHERE l.resource_type = N'APPLICATION' AND l.resource_description LIKE N'%Collections.ReceivablesRefresh%';
END TRY
BEGIN CATCH
    SELECT N'Section 5 needs VIEW SERVER STATE' AS Note, ERROR_NUMBER() AS ErrorNumber;
END CATCH;

-- 6. Which sessions does the application's login have on this server (proves which server the app really talks to)?
BEGIN TRY
    SELECT login_name, host_name, program_name, COUNT(*) AS Sessions, MAX(last_request_start_time) AS LastRequestStart
      FROM sys.dm_exec_sessions WHERE is_user_process = 1 AND login_name = N'TigerCsTicketing' GROUP BY login_name, host_name, program_name;
END TRY
BEGIN CATCH
    SELECT N'Section 6 needs VIEW SERVER STATE' AS Note, ERROR_NUMBER() AS ErrorNumber;
END CATCH;

-- 7. Is the linked server reachable with its current provider and login mapping? (a connectivity test only; no data, no distributed transaction)
BEGIN TRY
    EXEC sys.sp_testlinkedserver N'10.10.10.94';
    SELECT N'sp_testlinkedserver succeeded' AS LinkedServerTest;
END TRY
BEGIN CATCH
    SELECT N'sp_testlinkedserver failed' AS LinkedServerTest, ERROR_NUMBER() AS ErrorNumber, ERROR_MESSAGE() AS ErrorMessage;
END CATCH;

-- 8. Which application hosts run Hangfire against this database (BackgroundJobs:Enabled=true), and is the refresh job registered? Only the API host
--    registers jobs; the Web host never does. Server ids read <machine>:<pid>:<guid>. Empty/failed = Hangfire never ran against this database.
BEGIN TRY
    EXEC sys.sp_executesql N'SELECT Id AS HangfireServerId, StartedAt, LastHeartbeat, DATEDIFF(MINUTE, LastHeartbeat, SYSUTCDATETIME()) AS HeartbeatAgeMinutes FROM HangfireSla.[Server] ORDER BY LastHeartbeat DESC';
    EXEC sys.sp_executesql N'SELECT [Key], Field, LEFT(Value, 120) AS Value FROM HangfireSla.[Hash] WHERE [Key] = N''recurring-job:collections-receivables-refresh'' AND Field IN (N''Cron'', N''LastExecution'', N''NextExecution'', N''LastJobId'')';
END TRY
BEGIN CATCH
    SELECT N'Section 8: no Hangfire tables (HangfireSla) readable' AS Note, ERROR_NUMBER() AS ErrorNumber;
END CATCH;

/*
  9. (Manual, not run here) Time ONE company over a narrow window to separate "unreachable" from "slow", in SSMS, outside any transaction:
         SET REMOTE_PROC_TRANSACTIONS OFF;
         DECLARE @t datetime2 = SYSUTCDATETIME();
         INSERT INTO #t EXEC [10.10.10.94].[PACTRPT].[dbo].[p32AccountReceivables] @StartDate = '20260101', @EndDate = '20260131 23:59:59.997', @MinAmount = 0;
         SELECT DATEDIFF(SECOND, @t, SYSUTCDATETIME());
     (create #t with the 13 columns of V005 layout 1 first). Then widen the window. The scheduled refresh asks for 2000-01-01..2099-12-31 with paid
     instalments retained, which is the most expensive call the application makes.
*/
