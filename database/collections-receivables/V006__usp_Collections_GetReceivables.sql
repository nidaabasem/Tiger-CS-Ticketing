/*
  V006 - Local read procedures. The application NEVER calls PACT for a page view; it reads these.

  dbo.usp_Collections_GetReceivables  tower + due-date window + Due/Overdue filter over the CURRENT published snapshot
  dbo.usp_Collections_GetTowers       active towers for the searchable dropdown

  Result sets of GetReceivables (order is a contract with TigerCS.Infrastructure SnapshotPactReceivablesSource):
    1 scope      ScopeValid, TowerId, CompanyId, TowerNumber, TowerName   (ScopeValid = 0 for an unknown tower or a tower/company clash; nothing else is returned)
    2 coverage   one row per company in scope, always present even if never loaded (HasSnapshot = 0 means "not loaded", NOT "empty")
    3 rows       the filtered instalments
    4 unmatched  towers in scope that have receivables but no active CollectionsTowers row (reported, never dropped)

  Date semantics
    @FromDate/@ToDate     inclusive calendar days on the INSTALMENT DUE DATE: DueDate >= From AND DueDate < To + 1 day (To covers its whole day).
    @AsOfDate             decides only the classification month: OverDue = DueDate before the first day of the as-of month,
                          Due = DueDate inside the as-of month (including its later days), Outstanding = after the month end.
    @ReceivableClass      'DueOrOverdue' (default) | 'Due' | 'Overdue' | 'Any'. 'Any' applies only the window (campaign stages apply their own
                          confirmed rules in the application). Future instalments beyond the month end are never Overdue and never Due.
    Rows with Amount <= 0 or UnitId <= 0 never exist in the snapshot. Amounts are the CURRENT remaining balances, not balances as of a date.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetReceivables
    @TowerId         int          = NULL,
    @CompanyId       int          = NULL,
    @FromDate        date,
    @ToDate          date,
    @AsOfDate        date,
    @ReceivableClass varchar(20)  = 'DueOrOverdue'
AS
BEGIN
    SET NOCOUNT ON;
    IF @ReceivableClass NOT IN ('DueOrOverdue', 'Due', 'Overdue', 'Any') THROW 50030, N'Unknown receivable class.', 1;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;

    DECLARE @TowerFound bit = 0, @TowerCompany int, @TowerNumber nvarchar(20), @TowerName nvarchar(400);
    IF @TowerId IS NOT NULL
        SELECT @TowerFound = 1, @TowerCompany = t.CompanyId, @TowerNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))),
               @TowerName = CONVERT(nvarchar(400), t.TowerName)
          FROM dbo.CollectionsTowers t WHERE t.TowerId = @TowerId;

    DECLARE @Scope int = COALESCE(@TowerCompany, @CompanyId);
    DECLARE @ScopeValid bit = CASE WHEN @TowerId IS NULL THEN 1
                                   WHEN @TowerFound = 1 AND (@CompanyId IS NULL OR @CompanyId = @TowerCompany) THEN 1 ELSE 0 END;

    SELECT @ScopeValid AS ScopeValid, @TowerId AS TowerId, @TowerCompany AS CompanyId, @TowerNumber AS TowerNumber, @TowerName AS TowerName;
    IF @ScopeValid = 0
    BEGIN
        -- Keep the result-set contract so the caller can read to the end.
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        RETURN;
    END;

    SELECT v.CompanyId,
           CAST(CASE WHEN st.CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS HasSnapshot,
           st.LastSuccessUtc, st.LastAttemptUtc, st.LastAttemptStatus, st.LastErrorNumber, st.ConsecutiveFailures,
           ISNULL(st.SnapshotRowCount, 0) AS SnapshotRowCount, st.CoverageFromDate, st.CoverageThroughDate,
           ISNULL(st.ExcludedInvalidUnitRows, 0) AS ExcludedInvalidUnitRows, ISNULL(st.ExcludedInvalidUnitAmount, 0) AS ExcludedInvalidUnitAmount,
           ISNULL(st.ExcludedInvalidIdentityRows, 0) AS ExcludedInvalidIdentityRows, ISNULL(st.ExcludedInvalidIdentityAmount, 0) AS ExcludedInvalidIdentityAmount,
           ISNULL(st.ContradictoryStatusRows, 0) AS ContradictoryStatusRows, ISNULL(st.UnknownStatusRows, 0) AS UnknownStatusRows
      FROM (VALUES (4), (32)) v (CompanyId)
      LEFT JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = v.CompanyId
     WHERE @Scope IS NULL OR v.CompanyId = @Scope
     ORDER BY v.CompanyId;

    DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@AsOfDate), MONTH(@AsOfDate), 1);
    DECLARE @NextMonthStart date = DATEADD(MONTH, 1, @MonthStart);
    DECLARE @ToExclusive datetime = DATEADD(DAY, 1, CAST(@ToDate AS datetime));
    DECLARE @FromDt datetime = CAST(@FromDate AS datetime);

    SELECT s.CompanyId, s.TenantId, s.FullName, s.Mobile, s.Email, s.UnitId, s.UnitCode, s.ProjectCode,
           s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount, s.SourceStatus, s.TowerNumber,
           tw.TowerId, CONVERT(nvarchar(400), tw.TowerName) AS TowerName,
           CASE WHEN s.DueDate < @MonthStart THEN 'OverDue' WHEN s.DueDate < @NextMonthStart THEN 'Due' ELSE 'Outstanding' END AS ReceivableType
      FROM dbo.CollectionsReceivableSnapshot s
      JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = s.CompanyId AND st.CurrentRunId = s.RunId
      OUTER APPLY (SELECT TOP (1) t.TowerId, t.TowerName
                     FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = s.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     WHERE (@Scope IS NULL OR s.CompanyId = @Scope)
       AND (@TowerId IS NULL OR (s.CompanyId = @TowerCompany AND s.TowerNumber = @TowerNumber))
       AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive
       AND s.Amount > 0 AND s.UnitId > 0
       AND (   @ReceivableClass = 'Any'
            OR (@ReceivableClass = 'DueOrOverdue' AND s.DueDate < @NextMonthStart)
            OR (@ReceivableClass = 'Due'          AND s.DueDate >= @MonthStart AND s.DueDate < @NextMonthStart)
            OR (@ReceivableClass = 'Overdue'      AND s.DueDate < @MonthStart))
     ORDER BY s.CompanyId, s.TenantId, s.UnitId, s.DueDate, s.VoucherNumber, s.SnapshotRowId;

    ;WITH tower_status AS
    (
        SELECT s.CompanyId, s.TowerNumber, s.Amount,
               CASE WHEN s.TowerNumber IS NULL THEN 'NoTowerNumber'
                    WHEN tw.TowerId IS NULL THEN 'NoMatchingTower'
                    WHEN tw.IsActive = 0 THEN 'InactiveTower' END AS Reason
          FROM dbo.CollectionsReceivableSnapshot s
          JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = s.CompanyId AND st.CurrentRunId = s.RunId
          OUTER APPLY (SELECT TOP (1) t.TowerId, t.IsActive
                         FROM dbo.CollectionsTowers t
                        WHERE t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = s.TowerNumber
                        ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
         WHERE @Scope IS NULL OR s.CompanyId = @Scope
    )
    SELECT CompanyId, TowerNumber, Reason, COUNT_BIG(*) AS RowCount_, SUM(Amount) AS Amount
      FROM tower_status WHERE Reason IS NOT NULL
     GROUP BY CompanyId, TowerNumber, Reason
     ORDER BY CompanyId, TowerNumber;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetTowers
AS
BEGIN
    SET NOCOUNT ON;
    SELECT t.TowerId, LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) AS TowerNumber, CONVERT(nvarchar(400), t.TowerName) AS TowerName,
           t.CompanyId, CAST(t.IsActive AS bit) AS IsActive
      FROM dbo.CollectionsTowers t
     WHERE t.IsActive = 1 AND t.CompanyId IN (4, 32)
     ORDER BY TRY_CONVERT(int, t.TowerNumber), t.TowerNumber, t.TowerId;
END;
GO
