/*
  V006 - Local read procedures. The application NEVER calls PACT for a page view; it reads these.

    dbo.usp_Collections_GetCoverage          per-company snapshot state (freshness inputs, coverage, refresh progress)
    dbo.usp_Collections_GetUnmatchedTowers   receivables on towers that are not (active) in dbo.CollectionsTowers (reads the tiny per-run summary)
    dbo.usp_Collections_GetReceivablesPage   RECEIVABLES PAGE: filtering, per-apartment aggregation, counts and pagination all in SQL
    dbo.usp_Collections_GetInstalmentsPage   INSTALMENT-LEVEL list (Receivables page): payment status, month/range window, minimum amount, totals and pagination in SQL
    dbo.usp_Collections_GetReceivables       instalment rows for a window (used by Campaigns, whose per-unit review rules run in the application)
    dbo.usp_Collections_GetTowers            active towers for the dropdown

  Result sets of usp_Collections_GetReceivablesPage (order is a contract with SnapshotPactReceivablesSource):
    1 scope       ScopeValid, TowerId, CompanyId, TowerNumber, TowerName      (ScopeValid = 0: unknown tower or tower/company clash; nothing else is returned)
    2 coverage    one row per company in scope (usp_Collections_GetCoverage); HasSnapshot = 0 means "not loaded", NOT "empty"
    3 totals      TotalApartments, DueApartments, OverdueApartments           (over the filtered set, before paging)
    4 apartments  the requested page: one row per apartment with its due/overdue sums
    5 instalments the instalments of exactly those apartments
    6 unmatched   usp_Collections_GetUnmatchedTowers
  Result sets of usp_Collections_GetReceivables: 1 scope, 2 coverage, 3 rows (incl. OriginalAmount / PaidAmount - NULL unless the source returned and reconciled them; the review workflow derives Unpaid / Partially paid from them), 4 unmatched.
  Result sets of usp_Collections_GetInstalmentsPage: 1 scope, 2 coverage, 3 totals (incl. Unavailable), 4 the requested page of instalments, 5 unmatched.

  PAYMENT STATUS (usp_Collections_GetInstalmentsPage @PaymentFilter)
    outstanding (default)  remaining Amount > 0                       (unpaid + partially paid + rows whose status the source cannot tell)
    unpaid | partial       PaymentStatus Unpaid | PartiallyPaid       (only when the source returned original + paid amounts: BreakdownAvailable)
    paid                   PaymentStatus FullyPaid (remaining 0)      (only when fully paid instalments are retained: PaidRetained)
    all                    every retained instalment                  (only when PaidRetained)
    @MinAmount applies to outstanding / unpaid / partial (remaining >= min, inclusive) and is IGNORED for paid / all, so it can never hide a paid row.
    If the requested filter needs data the snapshot does not hold, Unavailable = 1 is returned with no rows (never an empty-looking list).
    Due/Overdue classification stays date based and separate from payment status: Overdue = due before the as-of month, Due = inside it, NotYetDue =
    after it, NotApplicable = fully paid (remaining 0).

  Semantics (unchanged business rules)
    @FromDate/@ToDate   inclusive calendar days on the INSTALMENT DUE DATE: DueDate >= From AND DueDate < To + 1 day.
    @MinAmount          an instalment is included only if its remaining unpaid Amount >= @MinAmount (inclusive). It is applied to the
                        instalment rows BEFORE anything is counted, summed or paged, so rows, counts, totals and pages always agree. The snapshot itself
                        always holds every positive balance (the refresh uses MinAmount = 0), so lowering the threshold never needs a PACT reload.
    @AsOfDate           only picks the classification month: Overdue = DueDate before the first day of that month; Due = inside that month
                        (including its later days); after the month end = neither (never overdue).
    @ReceivableClass    (GetReceivables) 'DueOrOverdue' | 'Due' | 'Overdue' | 'Any'. The page procedure always uses DueOrOverdue.
    Apartment           (CompanyId, TenantId, UnitId, trimmed UnitCode). Due/Overdue amount = SUM of that bucket's instalments, or NULL when two
                        instalments of the bucket share a due DATE (the source cannot tell a repeated allocation from a real instalment: needs review).
    Concurrency         readers touch only the rows of CollectionsReceivableCompanyState.CurrentRunId, found by index seek. The publisher inserts a NEW
                        run in small committed batches and flips the pointer in a short transaction, so a refresh never blocks a read and the previous data
                        stays readable until the flip. Dirty-read hints are not used anywhere (they would trade correctness for speed).
*/
/*
  RefreshInProgress = a run of this company is Running, started within the lease, and this company's own row of that run is not already
  finished. The lease must be longer than the application's refresh command timeout (Collections:ReceivablesSnapshot:RefreshCommandTimeoutSeconds,
  default 1800 s): a row older than that cannot belong to a live call (the call would have timed out), so a crashed run stops reading as
  "loading" after the lease instead of after hours, and it never hides a finished company (Succeeded / Failed) behind another company's run.
  Raise @RunLeaseMinutes together with RefreshCommandTimeoutSeconds.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCoverage @Scope int = NULL, @RunLeaseMinutes int = 40
AS
BEGIN
    SET NOCOUNT ON;
    SELECT v.CompanyId,
           CAST(CASE WHEN st.CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS HasSnapshot,
           st.LastSuccessUtc, st.LastAttemptUtc, st.LastAttemptStatus, st.LastErrorNumber, st.ConsecutiveFailures,
           ISNULL(st.SnapshotRowCount, 0) AS SnapshotRowCount, st.CoverageFromDate, st.CoverageThroughDate,
           ISNULL(st.ExcludedInvalidUnitRows, 0) AS ExcludedInvalidUnitRows, ISNULL(st.ExcludedInvalidUnitAmount, 0) AS ExcludedInvalidUnitAmount,
           ISNULL(st.ExcludedInvalidIdentityRows, 0) AS ExcludedInvalidIdentityRows, ISNULL(st.ExcludedInvalidIdentityAmount, 0) AS ExcludedInvalidIdentityAmount,
           ISNULL(st.ContradictoryStatusRows, 0) AS ContradictoryStatusRows, ISNULL(st.UnknownStatusRows, 0) AS UnknownStatusRows,
           CAST(ISNULL(st.PaidRetained, 0) AS bit) AS PaidRetained, CAST(ISNULL(st.BreakdownAvailable, 0) AS bit) AS BreakdownAvailable,
           ISNULL(st.UnclassifiedRows, 0) AS UnclassifiedRows,
           CAST(CASE WHEN run.RunId IS NULL THEN 0 ELSE 1 END AS bit) AS RefreshInProgress,
           run.StartedUtc AS RefreshStartedUtc,
           rc.Status AS RunCompanyStatus
      FROM (VALUES (4), (32)) v (CompanyId)
      LEFT JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = v.CompanyId
      OUTER APPLY (SELECT TOP (1) r.RunId, r.StartedUtc FROM dbo.CollectionsReceivableRun r
                      LEFT JOIN dbo.CollectionsReceivableRunCompany own ON own.RunId = r.RunId AND own.CompanyId = v.CompanyId
                    WHERE r.Status = 'Running' AND r.StartedUtc > DATEADD(MINUTE, -ISNULL(NULLIF(@RunLeaseMinutes, 0), 40), SYSUTCDATETIME())
                      AND (r.RequestedCompanyId IS NULL OR r.RequestedCompanyId = v.CompanyId)
                      AND (own.RunId IS NULL OR own.Status = 'Running')   -- this company has not finished in that run
                    ORDER BY r.StartedUtc DESC) run
      LEFT JOIN dbo.CollectionsReceivableRunCompany rc ON rc.RunId = run.RunId AND rc.CompanyId = v.CompanyId
     WHERE @Scope IS NULL OR v.CompanyId = @Scope
     ORDER BY v.CompanyId;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetUnmatchedTowers @Scope int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT x.CompanyId, x.TowerNumber, x.Reason, CAST(SUM(x.RowCnt) AS bigint) AS RowCount_, SUM(x.Amount) AS Amount
      FROM (SELECT sm.CompanyId, sm.TowerNumber, sm.RowCnt, sm.Amount,
                   CASE WHEN sm.TowerNumber IS NULL THEN 'NoTowerNumber'
                        WHEN tw.TowerId IS NULL THEN 'NoMatchingTower'
                        WHEN tw.IsActive = 0 THEN 'InactiveTower' END AS Reason
              FROM dbo.CollectionsReceivableTowerSummary sm
              JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = sm.CompanyId AND st.CurrentRunId = sm.RunId
              OUTER APPLY (SELECT TOP (1) t.TowerId, t.IsActive
                             FROM dbo.CollectionsTowers t
                            WHERE t.CompanyId = sm.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = sm.TowerNumber
                            ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
             WHERE @Scope IS NULL OR sm.CompanyId = @Scope) x
     WHERE x.Reason IS NOT NULL
     GROUP BY x.CompanyId, x.TowerNumber, x.Reason
     ORDER BY x.CompanyId, x.TowerNumber;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetReceivables
    @TowerId         int           = NULL,
    @CompanyId       int           = NULL,
    @FromDate        date,
    @ToDate          date,
    @AsOfDate        date,
    @ReceivableClass varchar(20)   = 'DueOrOverdue',
    @MinAmount       decimal(19,4) = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @ReceivableClass NOT IN ('DueOrOverdue', 'Due', 'Overdue', 'Any') THROW 50030, N'Unknown receivable class.', 1;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @MinAmount < 0 THROW 50033, N'@MinAmount must not be negative.', 1;

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
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        RETURN;
    END;

    EXEC dbo.usp_Collections_GetCoverage @Scope;

    DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@AsOfDate), MONTH(@AsOfDate), 1);
    DECLARE @NextMonthStart date = DATEADD(MONTH, 1, @MonthStart);
    DECLARE @ToExclusive datetime = DATEADD(DAY, 1, CAST(@ToDate AS datetime));
    DECLARE @FromDt datetime = CAST(@FromDate AS datetime);

    SELECT s.CompanyId, s.TenantId, s.FullName, s.Mobile, s.Email, s.UnitId, s.UnitCode, s.ProjectCode,
           s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount, s.SourceStatus, s.TowerNumber, s.OriginalAmount, s.PaidAmount,
           tw.TowerId, CONVERT(nvarchar(400), tw.TowerName) AS TowerName,
           CASE WHEN s.DueDate < @MonthStart THEN 'OverDue' WHEN s.DueDate < @NextMonthStart THEN 'Due' ELSE 'Outstanding' END AS ReceivableType
      FROM dbo.CollectionsReceivableCompanyState st
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = st.CompanyId AND s.RunId = st.CurrentRunId
      OUTER APPLY (SELECT TOP (1) t.TowerId, t.TowerName
                     FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = s.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive
       AND s.Amount > 0 AND s.Amount >= @MinAmount AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
       AND (   @ReceivableClass = 'Any'
            OR (@ReceivableClass = 'DueOrOverdue' AND s.DueDate < @NextMonthStart)
            OR (@ReceivableClass = 'Due'          AND s.DueDate >= @MonthStart AND s.DueDate < @NextMonthStart)
            OR (@ReceivableClass = 'Overdue'      AND s.DueDate < @MonthStart))
     ORDER BY s.CompanyId, s.TenantId, s.UnitId, s.DueDate, s.VoucherNumber, s.SnapshotRowId
     OPTION (RECOMPILE);

    EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetReceivablesPage
    @TowerId     int           = NULL,
    @CompanyId   int           = NULL,
    @FromDate    date,
    @ToDate      date,
    @AsOfDate    date,
    @MinAmount   decimal(19,4) = 0,
    @Status      varchar(10)   = 'all',           -- all | due | overdue (apartments having a Due / an Overdue instalment)
    @Search      nvarchar(200) = NULL,            -- name, customer id, e-mail, phone or unit code (contains, case-insensitive)
    @PhoneDigits nvarchar(200) = NULL,            -- digits of a phone-like search term (>= 3 digits, no letters), else NULL
    @PageNumber  int           = 1,
    @PageSize    int           = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @Status NOT IN ('all', 'due', 'overdue') THROW 50030, N'Unknown status.', 1;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @MinAmount < 0 THROW 50033, N'@MinAmount must not be negative.', 1;
    IF @PageNumber < 1 OR @PageSize < 1 OR @PageSize > 100 THROW 50034, N'Invalid page.', 1;

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
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        RETURN;
    END;

    EXEC dbo.usp_Collections_GetCoverage @Scope;

    DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@AsOfDate), MONTH(@AsOfDate), 1);
    DECLARE @NextMonthStart date = DATEADD(MONTH, 1, @MonthStart);
    DECLARE @ToExclusive datetime = DATEADD(DAY, 1, CAST(@ToDate AS datetime));
    DECLARE @FromDt datetime = CAST(@FromDate AS datetime);
    DECLARE @MonthStartDt datetime = CAST(@MonthStart AS datetime), @NextMonthDt datetime = CAST(@NextMonthStart AS datetime);
    -- Upper bound: Due/Overdue never reaches past the as-of month end.
    DECLARE @UpperDt datetime = CASE WHEN @ToExclusive < @NextMonthDt THEN @ToExclusive ELSE @NextMonthDt END;
    DECLARE @Like nvarchar(420) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
        SET @Like = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';

    -- 1. Apartment aggregates, straight from the index range of each company's CURRENT run (seek on CompanyId + RunId + DueDate).
    --    Two-level aggregation: instalments -> (apartment, bucket, due DAY) -> apartment. A bucket is ambiguous when it has more instalments than
    --    distinct due days; this avoids COUNT(DISTINCT), which sorts every group. Only narrow columns are carried; the display attributes
    --    (name, phone, ...) are fetched afterwards by primary key for the surviving apartments.
    CREATE TABLE #apt
    (
        CompanyId int NOT NULL, TenantId nvarchar(100) NOT NULL, UnitId bigint NOT NULL, UnitKey nvarchar(200) NOT NULL,
        FirstId bigint NOT NULL,
        DueRows int NOT NULL, OverRows int NOT NULL, DueAmt decimal(19,4) NOT NULL, OverAmt decimal(19,4) NOT NULL,
        DueDays int NOT NULL, OverDays int NOT NULL, Earliest date NOT NULL, Matches bit NOT NULL
    );
    INSERT #apt
    SELECT d.CompanyId, d.TenantId, d.UnitId, d.UnitCode, MIN(d.FirstId),
           SUM(CASE WHEN d.IsOver = 0 THEN d.Cnt ELSE 0 END), SUM(CASE WHEN d.IsOver = 1 THEN d.Cnt ELSE 0 END),
           SUM(CASE WHEN d.IsOver = 0 THEN d.Amt ELSE 0 END), SUM(CASE WHEN d.IsOver = 1 THEN d.Amt ELSE 0 END),
           SUM(CASE WHEN d.IsOver = 0 THEN 1 ELSE 0 END), SUM(CASE WHEN d.IsOver = 1 THEN 1 ELSE 0 END),
           MIN(d.Day_), MAX(d.Hit)
      FROM (SELECT s.CompanyId, s.TenantId COLLATE Latin1_General_BIN2 AS TenantId, s.UnitId, s.UnitCode COLLATE Latin1_General_BIN2 AS UnitCode,
                   CAST(CASE WHEN s.DueDate < @MonthStartDt THEN 1 ELSE 0 END AS bit) AS IsOver, CAST(s.DueDate AS date) AS Day_,
                   COUNT(*) AS Cnt, SUM(s.Amount) AS Amt, MIN(s.SnapshotRowId) AS FirstId,
                   CAST(CASE WHEN @Like IS NULL THEN 1
                             WHEN MAX(CASE WHEN s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\'
                                             OR s.Mobile LIKE @Like ESCAPE N'\' OR s.UnitCode LIKE @Like ESCAPE N'\'
                                             OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%')
                                           THEN 1 ELSE 0 END) = 1 THEN 1 ELSE 0 END AS int) AS Hit
              FROM dbo.CollectionsReceivableCompanyState st
              JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = st.CompanyId AND s.RunId = st.CurrentRunId
             WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL
               AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
               AND s.DueDate >= @FromDt AND s.DueDate < @UpperDt
               AND s.Amount > 0 AND s.Amount >= @MinAmount AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
             GROUP BY s.CompanyId, s.TenantId COLLATE Latin1_General_BIN2, s.UnitId, s.UnitCode COLLATE Latin1_General_BIN2,   -- an apartment is the exact (binary) tenant / code, as in the application
                      CASE WHEN s.DueDate < @MonthStartDt THEN 1 ELSE 0 END, CAST(s.DueDate AS date)) d
     GROUP BY d.CompanyId, d.TenantId, d.UnitId, d.UnitCode
    OPTION (RECOMPILE);

    -- 2. Filter (search + status), totals, page. Display attributes come from the apartment's first instalment, by primary key.
    SELECT a.CompanyId, a.TenantId, a.UnitId, a.UnitKey, a.DueRows, a.OverRows, a.DueAmt, a.OverAmt, a.DueDays, a.OverDays, a.Earliest,
           r.FullName, r.Mobile, r.Email, r.ProjectCode, r.TowerNumber
      INTO #sel
      FROM #apt a
      JOIN dbo.CollectionsReceivableSnapshot r ON r.SnapshotRowId = a.FirstId
     WHERE a.Matches = 1 AND (@Status = 'all' OR (@Status = 'due' AND a.DueRows > 0) OR (@Status = 'overdue' AND a.OverRows > 0));

    SELECT COUNT(*) AS TotalApartments, ISNULL(SUM(CASE WHEN DueRows > 0 THEN 1 ELSE 0 END), 0) AS DueApartments,
           ISNULL(SUM(CASE WHEN OverRows > 0 THEN 1 ELSE 0 END), 0) AS OverdueApartments FROM #sel;

    SELECT * INTO #page FROM #sel
     ORDER BY CASE WHEN OverRows > 0 THEN 0 ELSE 1 END, Earliest, FullName, CompanyId,
              TenantId COLLATE Latin1_General_BIN2, UnitKey COLLATE Latin1_General_BIN2, UnitId
     OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT p.CompanyId, p.TenantId, p.UnitId, p.UnitKey AS UnitCode, p.FullName, p.Mobile, p.Email, p.ProjectCode, p.TowerNumber,
           CONVERT(nvarchar(400), tw.TowerName) AS TowerName,
           p.DueRows, p.OverRows, p.DueAmt, p.OverAmt,
           CAST(CASE WHEN p.DueRows  <> p.DueDays  THEN 1 ELSE 0 END AS bit) AS DueAmbiguous,
           CAST(CASE WHEN p.OverRows <> p.OverDays THEN 1 ELSE 0 END AS bit) AS OverAmbiguous,
           p.Earliest
      FROM #page p
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = p.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = p.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     ORDER BY CASE WHEN p.OverRows > 0 THEN 0 ELSE 1 END, p.Earliest, p.FullName, p.CompanyId,
              p.TenantId COLLATE Latin1_General_BIN2, p.UnitKey COLLATE Latin1_General_BIN2, p.UnitId;

    SELECT s.CompanyId, s.TenantId, s.UnitId, s.UnitCode, s.ProjectCode, s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount, s.SourceStatus,
           CASE WHEN s.DueDate < @MonthStart THEN 'OverDue' ELSE 'Due' END AS ReceivableType
      FROM #page p
      JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = p.CompanyId
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = p.CompanyId AND s.RunId = st.CurrentRunId AND s.TenantId = p.TenantId AND s.UnitId = p.UnitId
     WHERE s.UnitCode = p.UnitKey AND s.TenantId COLLATE Latin1_General_BIN2 = p.TenantId COLLATE Latin1_General_BIN2 AND s.UnitCode COLLATE Latin1_General_BIN2 = p.UnitKey COLLATE Latin1_General_BIN2
       AND s.DueDate >= @FromDt AND s.DueDate < @UpperDt AND s.Amount > 0 AND s.Amount >= @MinAmount
     ORDER BY s.CompanyId, s.TenantId, s.UnitId, s.DueDate, s.VoucherNumber, s.SnapshotRowId
     OPTION (RECOMPILE);

    EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetInstalmentsPage
    @TowerId       int           = NULL,
    @CompanyId     int           = NULL,
    @FromDate      date,
    @ToDate        date,
    @AsOfDate      date,
    @MinAmount     decimal(19,4) = 0,
    @PaymentFilter varchar(12)   = 'outstanding',   -- outstanding | unpaid | partial | paid | all
    @Search        nvarchar(200) = NULL,
    @PhoneDigits   nvarchar(200) = NULL,
    @PageNumber    int           = 1,
    @PageSize      int           = 25
AS
BEGIN
    SET NOCOUNT ON;
    IF @PaymentFilter NOT IN ('outstanding', 'unpaid', 'partial', 'paid', 'all') THROW 50030, N'Unknown payment filter.', 1;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @MinAmount < 0 THROW 50033, N'@MinAmount must not be negative.', 1;
    IF @PageNumber < 1 OR @PageSize < 1 OR @PageSize > 200 THROW 50034, N'Invalid page.', 1;

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
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        RETURN;
    END;

    EXEC dbo.usp_Collections_GetCoverage @Scope;

    -- The requested view must be answerable from what the snapshot holds. Otherwise say so (Unavailable) instead of returning an empty-looking list.
    DECLARE @Unavailable bit = 0;
    IF @PaymentFilter IN ('paid', 'all') AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE (@Scope IS NULL OR CompanyId = @Scope) AND CurrentRunId IS NOT NULL AND PaidRetained = 0)
        SET @Unavailable = 1;
    IF @PaymentFilter IN ('unpaid', 'partial') AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE (@Scope IS NULL OR CompanyId = @Scope) AND CurrentRunId IS NOT NULL AND BreakdownAvailable = 0)
        SET @Unavailable = 1;

    DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@AsOfDate), MONTH(@AsOfDate), 1);
    DECLARE @NextMonthStart date = DATEADD(MONTH, 1, @MonthStart);
    DECLARE @MonthStartDt datetime = CAST(@MonthStart AS datetime), @NextMonthDt datetime = CAST(@NextMonthStart AS datetime);
    DECLARE @ToExclusive datetime = DATEADD(DAY, 1, CAST(@ToDate AS datetime));
    DECLARE @FromDt datetime = CAST(@FromDate AS datetime);
    DECLARE @Min decimal(19,4) = CASE WHEN @PaymentFilter IN ('paid', 'all') THEN 0 ELSE @MinAmount END;   -- never hide a paid row behind the minimum
    DECLARE @Like nvarchar(420) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
        SET @Like = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';

    IF @Unavailable = 1
    BEGIN
        SELECT CAST(0 AS int) AS TotalInstalments, CAST(0 AS decimal(19,4)) AS RemainingTotal, CAST(0 AS int) AS OverdueCount, CAST(0 AS decimal(19,4)) AS OverdueRemaining,
               CAST(0 AS int) AS DueCount, CAST(0 AS decimal(19,4)) AS DueRemaining, CAST(0 AS int) AS NotYetDueCount, CAST(0 AS decimal(19,4)) AS NotYetDueRemaining,
               CAST(0 AS int) AS FullyPaidCount, CAST(1 AS bit) AS Unavailable;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope;
        RETURN;
    END;

    -- Candidate rows: the index range of each company's CURRENT run (seek on CompanyId + RunId + DueDate), then the residual predicates.
    -- 1. Totals over the whole filtered set (before paging) so counts, sums and pages always agree.
    SELECT COUNT(*) AS TotalInstalments, ISNULL(SUM(s.Amount), 0) AS RemainingTotal,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate < @MonthStartDt THEN 1 ELSE 0 END), 0) AS OverdueCount,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate < @MonthStartDt THEN s.Amount ELSE 0 END), 0) AS OverdueRemaining,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @MonthStartDt AND s.DueDate < @NextMonthDt THEN 1 ELSE 0 END), 0) AS DueCount,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @MonthStartDt AND s.DueDate < @NextMonthDt THEN s.Amount ELSE 0 END), 0) AS DueRemaining,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @NextMonthDt THEN 1 ELSE 0 END), 0) AS NotYetDueCount,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @NextMonthDt THEN s.Amount ELSE 0 END), 0) AS NotYetDueRemaining,
           ISNULL(SUM(CASE WHEN s.Amount = 0 THEN 1 ELSE 0 END), 0) AS FullyPaidCount,
           CAST(0 AS bit) AS Unavailable
      FROM dbo.CollectionsReceivableCompanyState st
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = st.CompanyId AND s.RunId = st.CurrentRunId
     WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive
       AND (   (@PaymentFilter = 'outstanding' AND s.Amount > 0) OR (@PaymentFilter = 'unpaid' AND s.PaymentStatus = 'Unpaid')
            OR (@PaymentFilter = 'partial' AND s.PaymentStatus = 'PartiallyPaid') OR (@PaymentFilter = 'paid' AND s.PaymentStatus = 'FullyPaid') OR @PaymentFilter = 'all')
       AND s.Amount >= @Min AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
       AND (@Like IS NULL OR s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\' OR s.Mobile LIKE @Like ESCAPE N'\'
                          OR s.UnitCode LIKE @Like ESCAPE N'\' OR s.VoucherNumber LIKE @Like ESCAPE N'\'
                          OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%'))
    OPTION (RECOMPILE);

    -- 2. The requested page of instalments (oldest due date first).
    SELECT s.CompanyId, s.TowerNumber, CONVERT(nvarchar(400), tw.TowerName) AS TowerName, s.UnitId, s.UnitCode, s.ProjectCode, s.TenantId, s.FullName, s.Mobile, s.Email,
           s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount AS RemainingAmount, s.OriginalAmount, s.PaidAmount, s.PaymentStatus, s.SourceStatus,
           CASE WHEN s.Amount = 0 THEN 'NotApplicable' WHEN s.DueDate < @MonthStartDt THEN 'Overdue' WHEN s.DueDate < @NextMonthDt THEN 'Due' ELSE 'NotYetDue' END AS Classification
      FROM dbo.CollectionsReceivableCompanyState st
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = st.CompanyId AND s.RunId = st.CurrentRunId
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = s.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive
       AND (   (@PaymentFilter = 'outstanding' AND s.Amount > 0) OR (@PaymentFilter = 'unpaid' AND s.PaymentStatus = 'Unpaid')
            OR (@PaymentFilter = 'partial' AND s.PaymentStatus = 'PartiallyPaid') OR (@PaymentFilter = 'paid' AND s.PaymentStatus = 'FullyPaid') OR @PaymentFilter = 'all')
       AND s.Amount >= @Min AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
       AND (@Like IS NULL OR s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\' OR s.Mobile LIKE @Like ESCAPE N'\'
                          OR s.UnitCode LIKE @Like ESCAPE N'\' OR s.VoucherNumber LIKE @Like ESCAPE N'\'
                          OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%'))
     ORDER BY s.DueDate, s.CompanyId, s.UnitCode COLLATE Latin1_General_BIN2, s.TenantId COLLATE Latin1_General_BIN2, s.VoucherNumber COLLATE Latin1_General_BIN2, s.SnapshotRowId
     OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY
     OPTION (RECOMPILE);

    EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope;
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
