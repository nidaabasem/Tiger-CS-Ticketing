/*
  Short verification after the deployment (TigerCsTicketing).  READ-ONLY.  Every row of the result must say OK.
*/
SET NOCOUNT ON;
DECLARE @r TABLE (Check_ nvarchar(120), Result varchar(8), Detail nvarchar(300));
INSERT @r SELECT N'both companies published by the NEW pipeline', CASE WHEN COUNT(*) = 2 THEN 'OK' ELSE 'FAIL' END, CONCAT(COUNT(*), N' of 2 companies have a published run')
  FROM dbo.CollectionsRcvCompany WHERE CompanyId IN (4, 32) AND CurrentRunId IS NOT NULL AND PublishedRows > 0;
INSERT @r SELECT N'newest attempt of each company succeeded', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'FAIL' END, STRING_AGG(CONCAT(N'company ', CompanyId, N': ', LastStatus, N' ', ISNULL(LastError, N'')), N'; ')
  FROM dbo.CollectionsRcvCompany WHERE LastStatus <> 'Succeeded';
INSERT @r SELECT N'nothing stuck in Running', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'CHECK' END, CONCAT(COUNT(*), N' Running row(s); run tools/diagnose_receivables_refresh.sql section 4') FROM dbo.CollectionsRcvRun WHERE Status = 'Running' AND StartedUtc < DATEADD(MINUTE, -45, SYSUTCDATETIME());
INSERT @r SELECT N'refreshed within the last 3 hours (MaxAgeMinutes 180)', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'FAIL' END, STRING_AGG(CONCAT(N'company ', CompanyId, N' last success ', ISNULL(CONVERT(varchar(19), LastSuccessUtc, 120), 'never'), N' UTC'), N'; ')
  FROM dbo.CollectionsRcvCompany WHERE LastSuccessUtc IS NULL OR LastSuccessUtc < DATEADD(MINUTE, -180, SYSUTCDATETIME());
INSERT @r SELECT N'no cancelled or invalid unit published', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'FAIL' END, CONCAT(COUNT(*), N' row(s)') FROM dbo.CollectionsRcvLine WHERE UnitCode LIKE N'%*%' OR UnitId <= 0 OR LTRIM(RTRIM(UnitCode)) IN (N'', N'0') OR Amount <= 0;
INSERT @r SELECT N'only companies 4 and 32 (never 7)', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'FAIL' END, CONCAT(COUNT(*), N' row(s)') FROM dbo.CollectionsRcvLine WHERE CompanyId NOT IN (4, 32);
INSERT @r SELECT N'published lines belong to the current run of their company only', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'CHECK' END, CONCAT(COUNT(*), N' superseded line(s) waiting to be cleaned')
  FROM dbo.CollectionsRcvLine l JOIN dbo.CollectionsRcvCompany c ON c.CompanyId = l.CompanyId WHERE l.RunId <> c.CurrentRunId;
INSERT @r SELECT N'the old schedule is gone (Hangfire)', CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'FAIL' END, CASE WHEN COUNT(*) = 0 THEN N'old recurring job collections-receivables-refresh is not registered' ELSE N'old recurring job collections-receivables-refresh is STILL registered (old application still running?)' END
  FROM HangfireSla.[Set] WHERE [Key] = N'recurring-jobs' AND [Value] = N'collections-receivables-refresh';
INSERT @r SELECT N'two-hourly recurring jobs registered (Hangfire)', CASE WHEN COUNT(*) = 2 THEN 'OK' ELSE 'FAIL' END, CONCAT(COUNT(*), N' of 2 recurring jobs found (needs BackgroundJobs:Enabled=true)')
  FROM HangfireSla.[Set] WHERE [Key] = N'recurring-jobs' AND [Value] IN (N'collections-receivables-refresh-4', N'collections-receivables-refresh-32');
SELECT * FROM @r;
SELECT c.CompanyId, c.PublishedRows, c.LastSuccessUtc, COUNT(DISTINCT CONCAT(l.TenantId, N'|', l.UnitId, N'|', l.UnitCode)) AS Apartments, SUM(l.Amount) AS RemainingTotal,
       SUM(CASE WHEN l.DueDate >= DATEFROMPARTS(YEAR(CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date)), MONTH(CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date)), 1)
                 AND l.DueDate < DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date)), MONTH(CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date)), 1)) THEN l.Amount ELSE 0 END) AS DueThisMonth,
       SUM(CASE WHEN l.DueDate < DATEFROMPARTS(YEAR(CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date)), MONTH(CAST(SWITCHOFFSET(SYSDATETIMEOFFSET(), '+04:00') AS date)), 1) THEN l.Amount ELSE 0 END) AS Overdue
  FROM dbo.CollectionsRcvCompany c LEFT JOIN dbo.CollectionsRcvLine l ON l.CompanyId = c.CompanyId AND l.RunId = c.CurrentRunId GROUP BY c.CompanyId, c.PublishedRows, c.LastSuccessUtc ORDER BY c.CompanyId;
