/*
  Pre-cleanup verification, part 2 (TigerCsTicketing).  READ-ONLY.  Run after the first real load of BOTH companies.

  1  independent recount: for each company, straight from the PUBLISHED lines (no stored procedure involved): apartments, payments, Due (inside the current Dubai month), Overdue (before its first day), Total;
     and the per-company figures of the last run (RawRows, PublishedRows, the excluded counts, fetch / publish ms) next to the result of tools/test_pact_fetch.sql.
  2  sample apartments (the one with the largest Total, one with the most payments, one with two apartments for the same customer, one with no mobile and no e-mail - per company) with the
     EXACT values the Receivables page must show: Due, Overdue, Total, number of payments and the payments newest first.  Compare each with the page and with the stored procedure:
         EXEC dbo.usp_CollectionsRcv_GetUnits @Today = '<today>', @Search = N'<unit code>', @MinTotal = NULL;   -- result set 4 = the row, result set 5 = its payments; sum(set 5) = Total
  3  the recurring jobs: cron, time zone, next and last execution in Dubai time.
  (The automatic check that every apartment's Total equals the sum of its details is tools/verify_details_total.sql.)
  Today = the Dubai date at the moment the script runs.
*/
SET NOCOUNT ON;
DECLARE @Today date = CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date);
DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1), @NextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1));
SELECT @Today AS TodayDubai, @MonthStart AS MonthStart, @NextMonthStart AS NextMonthStart;

-- 1. recount
SELECT l.CompanyId, COUNT(DISTINCT CONCAT(l.TenantId, N'|', l.UnitId, N'|', l.UnitCode)) AS Apartments, COUNT_BIG(*) AS Payments,
       SUM(CASE WHEN l.DueDate < @NextMonthStart THEN l.Amount ELSE 0 END) AS TotalDueOrOverdue,
       SUM(CASE WHEN l.DueDate >= @MonthStart AND l.DueDate < @NextMonthStart THEN l.Amount ELSE 0 END) AS Due, SUM(CASE WHEN l.DueDate < @MonthStart THEN l.Amount ELSE 0 END) AS Overdue,
       SUM(CASE WHEN l.DueDate >= @NextMonthStart THEN l.Amount ELSE 0 END) AS NextMonthOnwardsStoredNeverShown
  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId = l.RunId GROUP BY l.CompanyId ORDER BY l.CompanyId;
SELECT r.CompanyId, r.StartedUtc, r.Status, r.Layout, r.SourceFromDate, r.SourceThroughDate, r.RawRows, r.PublishedRows, r.ExcludedSettledRows, r.ExcludedInvalidRows, r.ExcludedCancelledRows,
       r.ExcludedCancelledAmount, r.FetchMs, r.PublishMs
  FROM dbo.CollectionsRcvRun r JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = r.CompanyId AND c.CurrentRunId = r.RunId ORDER BY r.CompanyId;

-- 2. sample apartments with their expected page values
CREATE TABLE #apt (CompanyId int, TenantId nvarchar(100) COLLATE Latin1_General_BIN2, UnitId bigint, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2, Due decimal(19,4), Overdue decimal(19,4), Total decimal(19,4), Payments int, Kind varchar(40));
INSERT #apt (CompanyId, TenantId, UnitId, UnitCode, Due, Overdue, Total, Payments)
SELECT l.CompanyId, l.TenantId, l.UnitId, l.UnitCode, SUM(CASE WHEN l.DueDate >= @MonthStart THEN l.Amount ELSE 0 END), SUM(CASE WHEN l.DueDate < @MonthStart THEN l.Amount ELSE 0 END), SUM(l.Amount), COUNT(*)
  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId = l.RunId WHERE l.DueDate < @NextMonthStart GROUP BY l.CompanyId, l.TenantId, l.UnitId, l.UnitCode;
CREATE TABLE #sample (CompanyId int, TenantId nvarchar(100) COLLATE Latin1_General_BIN2, UnitId bigint, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2, Kind varchar(40));
DECLARE @co TABLE (CompanyId int); INSERT @co VALUES (4), (32);
INSERT #sample SELECT TOP (1) a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, 'largest Total' FROM #apt a WHERE a.CompanyId = 4 ORDER BY a.Total DESC;
INSERT #sample SELECT TOP (1) a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, 'largest Total' FROM #apt a WHERE a.CompanyId = 32 ORDER BY a.Total DESC;
INSERT #sample SELECT TOP (1) a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, 'most payments' FROM #apt a WHERE a.CompanyId = 4 ORDER BY a.Payments DESC, a.Total DESC;
INSERT #sample SELECT TOP (1) a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, 'most payments' FROM #apt a WHERE a.CompanyId = 32 ORDER BY a.Payments DESC, a.Total DESC;
INSERT #sample SELECT TOP (2) a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, 'customer with 2+ apartments' FROM #apt a
  WHERE a.TenantId IN (SELECT TenantId FROM #apt GROUP BY CompanyId, TenantId HAVING COUNT(*) > 1) ORDER BY a.CompanyId, a.TenantId, a.UnitCode;
INSERT #sample SELECT TOP (2) a.CompanyId, a.TenantId, a.UnitId, a.UnitCode, 'no mobile and no e-mail' FROM #apt a
  WHERE NOT EXISTS (SELECT 1 FROM dbo.CollectionsRcvLine x JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = x.CompanyId AND c.CurrentRunId = x.RunId
                     WHERE x.CompanyId = a.CompanyId AND x.TenantId = a.TenantId AND x.UnitId = a.UnitId AND x.UnitCode = a.UnitCode AND (x.Mobile <> N'' OR x.Email <> N'')) ORDER BY a.Total DESC;
SELECT s.Kind, a.CompanyId, a.TenantId, a.UnitCode, a.Due, a.Overdue, a.Total, a.Payments,
       CONCAT(N'EXEC dbo.usp_CollectionsRcv_GetUnits @Today = ''', CONVERT(char(10), @Today, 23), N''', @CompanyId = ', a.CompanyId, N', @Search = N''', a.UnitCode, N''', @MinTotal = NULL;') AS RunThis
  FROM #sample s JOIN #apt a ON a.CompanyId = s.CompanyId AND a.TenantId = s.TenantId AND a.UnitId = s.UnitId AND a.UnitCode = s.UnitCode ORDER BY a.CompanyId, s.Kind;
SELECT s.Kind, l.CompanyId, l.UnitCode, l.VoucherNumber, l.DueDate, l.Amount, CASE WHEN l.DueDate < @MonthStart THEN 'Overdue' ELSE 'Due' END AS Classification, DATEDIFF(DAY, l.DueDate, @Today) AS DaysLate
  FROM #sample s JOIN dbo.CollectionsRcvLine l ON l.CompanyId = s.CompanyId AND l.TenantId = s.TenantId AND l.UnitId = s.UnitId AND l.UnitCode = s.UnitCode
  JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId AND c.CurrentRunId = l.RunId WHERE l.DueDate < @NextMonthStart ORDER BY l.CompanyId, s.Kind, l.DueDate DESC, l.VoucherNumber;

-- 3. the recurring jobs (needs BackgroundJobs:Enabled = true on the application)
SELECT REPLACE(h.[Key], N'recurring-job:', N'') AS RecurringJob,
       MAX(CASE WHEN h.Field = N'Cron' THEN h.Value END) AS Cron, MAX(CASE WHEN h.Field = N'TimeZoneId' THEN h.Value END) AS TimeZone,
       DATEADD(HOUR, 4, DATEADD(SECOND, TRY_CAST(MAX(CASE WHEN h.Field = N'NextExecution' THEN h.Value END) AS bigint) / 1000, '19700101')) AS NextExecutionDubai,
       DATEADD(HOUR, 4, TRY_CAST(MAX(CASE WHEN h.Field = N'LastExecution' THEN h.Value END) AS datetime2)) AS LastExecutionDubai,
       MAX(CASE WHEN h.Field = N'LastJobId' THEN h.Value END) AS LastJobId
  FROM HangfireSla.Hash h WHERE h.[Key] LIKE N'recurring-job:collections-receivables-refresh%' GROUP BY h.[Key] ORDER BY h.[Key];
