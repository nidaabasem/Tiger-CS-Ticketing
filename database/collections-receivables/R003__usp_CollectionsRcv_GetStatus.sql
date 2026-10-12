/*
  R003 - Per-company state of the receivables snapshot, as the pages show it.  EXEC dbo.usp_CollectionsRcv_GetStatus;  or  @CompanyId = 32.

  First it closes attempts whose session is gone (usp_CollectionsRcv_ReapDeadRuns: tries the company's application lock - if it can take it, nothing is
  refreshing that company).  So IsLoading is TRUE only while a refresh of THAT company is really executing: it ends when that company's attempt ends, and a
  company that finished, failed or was abandoned is never reported as loading because of another company's (or an older) run.

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
    SELECT CompanyId, CAST(CASE WHEN CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS HasSnapshot, LastSuccessUtc, PublishedRows,
           LastAttemptUtc, LastFinishedUtc, LastStatus, LastErrorNumber, LastError, ConsecutiveFailures,
           CAST(CASE WHEN LastStatus = 'Running' THEN 1 ELSE 0 END AS bit) AS IsLoading
      FROM dbo.CollectionsRcvCompany
     WHERE @CompanyId IS NULL OR CompanyId = @CompanyId
     ORDER BY CompanyId;
END;
GO
