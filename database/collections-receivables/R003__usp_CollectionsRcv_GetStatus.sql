/*
  R003 - Per-company state of the receivables snapshot, as the pages show it.  EXEC dbo.usp_CollectionsRcv_GetStatus;  or  @CompanyId = 32.

  First it closes attempts whose session is gone (usp_CollectionsRcv_ReapDeadRuns: tries the company's application lock - if it can take it, nothing is
  refreshing that company).  So IsLoading is TRUE only while a refresh of THAT company is really executing: it ends when that company's attempt ends, and a
  company that finished, failed or was abandoned is never reported as loading because of another company's (or an older) run.

  EVIDENCE COLUMNS (added later, appended so older readers are unaffected): LastFetchMs / LastRawRows of the LAST ATTEMPT's run (NULL fetch time on a failed run = the PACT call did not finish,
  i.e. the failure is in the linked-server read; set fetch time + failure = validation or publish).  The application turns them into the phase shown on the page.

  One row per company: HasSnapshot (a run is published; FALSE = "not loaded", never "zero receivables"), LastSuccessUtc, PublishedRows, the last attempt
  (LastStatus Never | Running | Succeeded | Failed | Abandoned, started / finished, error number + message) and ConsecutiveFailures.
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetStatus
    @CompanyId int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50001, N'Company must be 4, 32 or NULL.', 1;
    EXEC dbo.usp_CollectionsRcv_ReapDeadRuns @CompanyId = @CompanyId;
    SELECT c.CompanyId, CAST(CASE WHEN c.CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS HasSnapshot, c.LastSuccessUtc, c.PublishedRows,
           c.LastAttemptUtc, c.LastFinishedUtc, c.LastStatus, c.LastErrorNumber, c.LastError, c.ConsecutiveFailures,
           CAST(CASE WHEN c.LastStatus = 'Running' THEN 1 ELSE 0 END AS bit) AS IsLoading,
           r.FetchMs AS LastFetchMs, r.RawRows AS LastRawRows
      FROM dbo.CollectionsRcvCompany c
      LEFT JOIN dbo.CollectionsRcvRun r ON r.RunId = c.LastRunId
     WHERE @CompanyId IS NULL OR c.CompanyId = @CompanyId
     ORDER BY c.CompanyId;
END;
GO
