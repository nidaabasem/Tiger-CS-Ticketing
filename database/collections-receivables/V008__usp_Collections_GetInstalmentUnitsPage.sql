/*
  V008 - Receivables "By unit" view: dbo.usp_Collections_GetInstalmentUnitsPage

  The same filters as dbo.usp_Collections_GetInstalmentsPage (tower, due-date window, payment status, minimum outstanding amount, search), but grouped
  per UNIT IN SQL BEFORE PAGING, so a unit is never split over two pages and every matching instalment of a listed unit is returned with it.
  A unit is (CompanyId, TenantId, UnitId, UnitCode) compared binary-exactly: different companies and different customers are never merged.

  Result sets
    1 scope        ScopeValid, TowerId, CompanyId, TowerNumber, TowerName
    2 coverage     usp_Collections_GetCoverage
    3 totals       same columns as the instalment view (over ALL matching instalments) + UnitCount + Unavailable
    4 units        the requested page of units: key, tower, customer, InstalmentCount, RemainingTotal, OldestDueDate (oldest first)
    5 instalments  every matching instalment of exactly those units (same columns as the instalment view), ordered unit, due date, voucher
    6 unmatched    usp_Collections_GetUnmatchedTowers
  Sum of InstalmentCount over all units = Totals.TotalInstalments; RemainingTotal likewise: both views always agree.

  Day-based revision (Receivables page): @ClassifyByDay = 1 classifies by the DAY of @AsOfDate (pass today, Dubai): Overdue = due before it, Due = due on it,
  Not yet due (shown as Upcoming) = due after it. The default (0) keeps the month rule (@AsOfDate's calendar month) for older callers.
  @StatusFilter (overdue | due) keeps the units that have at least one instalment of that class; the listed instalments of a kept unit are still ALL of its
  matching instalments. Rows with UnitID <= 0, a blank / "0" unit code or a '*' in the unit code (cancelled apartment, e.g. 513*) are never listed.
  @MinTotal (NULL = none) keeps units whose summed remaining amount is greater than it. The application passes @ToDate = today, so only Due + Overdue is read. The search also matches tower number, project code and tower name.
  Idempotent (CREATE OR ALTER); no table changes; needs V002-V006 (the snapshot tables and usp_Collections_GetCoverage / GetUnmatchedTowers).
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetInstalmentUnitsPage
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
    @PageSize      int           = 25,
    @ClassifyByDay bit           = 0,
    @StatusFilter  varchar(10)   = NULL,            -- NULL | overdue | due (needs @ClassifyByDay for the day rule)
    @MinTotal      decimal(19,4) = NULL             -- keep units whose summed remaining amount is GREATER than this; NULL = no minimum
AS
BEGIN
    SET NOCOUNT ON;
    IF @PaymentFilter NOT IN ('outstanding', 'unpaid', 'partial', 'paid', 'all') THROW 50030, N'Unknown payment filter.', 1;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @MinAmount < 0 THROW 50033, N'@MinAmount must not be negative.', 1;
    IF @PageNumber < 1 OR @PageSize < 1 OR @PageSize > 100 THROW 50034, N'Invalid page.', 1;
    IF @StatusFilter IS NOT NULL AND @StatusFilter NOT IN ('overdue', 'due') THROW 50035, N'Unknown status filter.', 1;

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

    DECLARE @Unavailable bit = 0;
    IF @PaymentFilter IN ('paid', 'all') AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE (@Scope IS NULL OR CompanyId = @Scope) AND CurrentRunId IS NOT NULL AND PaidRetained = 0)
        SET @Unavailable = 1;
    IF @PaymentFilter IN ('unpaid', 'partial') AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE (@Scope IS NULL OR CompanyId = @Scope) AND CurrentRunId IS NOT NULL AND BreakdownAvailable = 0)
        SET @Unavailable = 1;

    -- "Month" boundaries of the classification: the calendar month of @AsOfDate, or - day-based - exactly the day @AsOfDate.
    DECLARE @MonthStart date = CASE WHEN @ClassifyByDay = 1 THEN @AsOfDate ELSE DATEFROMPARTS(YEAR(@AsOfDate), MONTH(@AsOfDate), 1) END;
    DECLARE @NextMonthStart date = CASE WHEN @ClassifyByDay = 1 THEN DATEADD(DAY, 1, @AsOfDate) ELSE DATEADD(MONTH, 1, @MonthStart) END;
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
               CAST(0 AS int) AS FullyPaidCount, CAST(0 AS int) AS UnitCount, CAST(1 AS bit) AS Unavailable;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope;
        RETURN;
    END;

    -- 1. Group the matching instalments per unit (binary-exact tenant / unit code), carrying what the totals need, so everything below is derived from this one scan.
    CREATE TABLE #u
    (
        CompanyId int NOT NULL, TenantId nvarchar(100) COLLATE Latin1_General_BIN2 NOT NULL, UnitId bigint NOT NULL, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
        Cnt int NOT NULL, Rem decimal(19,4) NOT NULL, Oldest datetime NOT NULL, FirstId bigint NOT NULL,
        OverCnt int NOT NULL, OverAmt decimal(19,4) NOT NULL, DueCnt int NOT NULL, DueAmt decimal(19,4) NOT NULL, NotCnt int NOT NULL, NotAmt decimal(19,4) NOT NULL, PaidCnt int NOT NULL
    );
    INSERT #u WITH (TABLOCK)
    SELECT s.CompanyId, s.TenantId COLLATE Latin1_General_BIN2, s.UnitId, s.UnitCode COLLATE Latin1_General_BIN2,
           COUNT(*), SUM(s.Amount), MIN(s.DueDate), MIN(s.SnapshotRowId),
           SUM(CASE WHEN s.Amount > 0 AND s.DueDate < @MonthStartDt THEN 1 ELSE 0 END), SUM(CASE WHEN s.Amount > 0 AND s.DueDate < @MonthStartDt THEN s.Amount ELSE 0 END),
           SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @MonthStartDt AND s.DueDate < @NextMonthDt THEN 1 ELSE 0 END),
           SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @MonthStartDt AND s.DueDate < @NextMonthDt THEN s.Amount ELSE 0 END),
           SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @NextMonthDt THEN 1 ELSE 0 END), SUM(CASE WHEN s.Amount > 0 AND s.DueDate >= @NextMonthDt THEN s.Amount ELSE 0 END),
           SUM(CASE WHEN s.Amount = 0 THEN 1 ELSE 0 END)
      FROM dbo.CollectionsReceivableCompanyState st
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = st.CompanyId AND s.RunId = st.CurrentRunId
     WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive
       AND (   (@PaymentFilter = 'outstanding' AND s.Amount > 0) OR (@PaymentFilter = 'unpaid' AND s.PaymentStatus = 'Unpaid')
            OR (@PaymentFilter = 'partial' AND s.PaymentStatus = 'PartiallyPaid') OR (@PaymentFilter = 'paid' AND s.PaymentStatus = 'FullyPaid') OR @PaymentFilter = 'all')
       AND s.Amount >= @Min AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
       AND (@Like IS NULL OR s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\' OR s.Mobile LIKE @Like ESCAPE N'\'
                          OR s.UnitCode LIKE @Like ESCAPE N'\' OR s.VoucherNumber LIKE @Like ESCAPE N'\' OR s.TowerNumber LIKE @Like ESCAPE N'\' OR s.ProjectCode LIKE @Like ESCAPE N'\'
                          OR EXISTS (SELECT 1 FROM dbo.CollectionsTowers tq WHERE tq.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), tq.TowerNumber))) = s.TowerNumber
                                      AND CONVERT(nvarchar(400), tq.TowerName) LIKE @Like ESCAPE N'\')
                          OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%'))
     GROUP BY s.CompanyId, s.TenantId COLLATE Latin1_General_BIN2, s.UnitId, s.UnitCode COLLATE Latin1_General_BIN2
    OPTION (RECOMPILE);

    -- 1b. Status filter: keep the units that have at least one instalment of the chosen class (totals, paging and counts below then agree with it).
    IF @StatusFilter = 'overdue'  DELETE FROM #u WHERE OverCnt = 0;
    IF @StatusFilter = 'due'      DELETE FROM #u WHERE DueCnt = 0;
    -- Minimum Total: on the unit's SUM of the matching instalments (not on each instalment), before totals, counts and paging.
    IF @MinTotal IS NOT NULL DELETE FROM #u WHERE Rem <= @MinTotal;

    -- 2. Totals over the whole filtered set (all pages).
    SELECT ISNULL(SUM(Cnt), 0) AS TotalInstalments, ISNULL(SUM(Rem), 0) AS RemainingTotal,
           ISNULL(SUM(OverCnt), 0) AS OverdueCount, ISNULL(SUM(OverAmt), 0) AS OverdueRemaining, ISNULL(SUM(DueCnt), 0) AS DueCount, ISNULL(SUM(DueAmt), 0) AS DueRemaining,
           ISNULL(SUM(NotCnt), 0) AS NotYetDueCount, ISNULL(SUM(NotAmt), 0) AS NotYetDueRemaining, ISNULL(SUM(PaidCnt), 0) AS FullyPaidCount,
           COUNT(*) AS UnitCount, CAST(0 AS bit) AS Unavailable
      FROM #u;

    -- 3. The requested page of units, oldest due date first (then company, customer, unit: a total order, so pages never overlap or skip).
    SELECT p.CompanyId, p.TenantId COLLATE DATABASE_DEFAULT AS TenantId, p.UnitId, p.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode,
           r.FullName, r.TowerNumber, CONVERT(nvarchar(400), tw.TowerName) AS TowerName,
           p.Cnt AS InstalmentCount, p.Rem AS RemainingTotal, p.Oldest AS OldestDueDate,
           -- The CRM side of the unit (V010; NULL = CRM holds no eligible sale or is not loaded): customers = 1 -> that customer's contact, > 1 -> ambiguous, nobody chosen.
           l.Customers AS CrmCustomers, CASE WHEN l.Customers = 1 THEN l.CustomerId END AS CrmCustomerId, CASE WHEN l.Customers = 1 THEN l.FullName END AS CrmName,
           CASE WHEN l.Customers = 1 THEN l.PhoneNorm END AS CrmPhone, CASE WHEN l.Customers = 1 THEN l.EmailNorm END AS CrmEmail
      FROM (SELECT * FROM #u ORDER BY Oldest, CompanyId, TenantId, UnitCode, UnitId
             OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY) p
      JOIN dbo.CollectionsReceivableSnapshot r ON r.SnapshotRowId = p.FirstId
      LEFT JOIN dbo.fn_Collections_CrmUnitLinks() l ON l.CompanyId = p.CompanyId AND l.UnitKey = dbo.fn_CollectionsUnitKey(p.UnitCode) COLLATE Latin1_General_BIN2
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = r.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = r.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     ORDER BY p.Oldest, p.CompanyId, p.TenantId, p.UnitCode, p.UnitId;

    -- 4. Every matching instalment of exactly those units (same predicates as the grouping, so counts and amounts add up).
    SELECT s.CompanyId, s.TowerNumber, CONVERT(nvarchar(400), tw.TowerName) AS TowerName, s.UnitId, s.UnitCode, s.ProjectCode, s.TenantId, s.FullName, s.Mobile, s.Email,
           s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount AS RemainingAmount, s.OriginalAmount, s.PaidAmount, s.PaymentStatus, s.SourceStatus,
           CASE WHEN s.Amount = 0 THEN 'NotApplicable' WHEN s.DueDate < @MonthStartDt THEN 'Overdue' WHEN s.DueDate < @NextMonthDt THEN 'Due' ELSE 'NotYetDue' END AS Classification
      FROM (SELECT * FROM #u ORDER BY Oldest, CompanyId, TenantId, UnitCode, UnitId
             OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY) p
      JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = p.CompanyId
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = p.CompanyId AND s.RunId = st.CurrentRunId AND s.TenantId = p.TenantId COLLATE DATABASE_DEFAULT AND s.UnitId = p.UnitId
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = s.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     WHERE s.TenantId COLLATE Latin1_General_BIN2 = p.TenantId AND s.UnitCode COLLATE Latin1_General_BIN2 = p.UnitCode
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive
       AND (   (@PaymentFilter = 'outstanding' AND s.Amount > 0) OR (@PaymentFilter = 'unpaid' AND s.PaymentStatus = 'Unpaid')
            OR (@PaymentFilter = 'partial' AND s.PaymentStatus = 'PartiallyPaid') OR (@PaymentFilter = 'paid' AND s.PaymentStatus = 'FullyPaid') OR @PaymentFilter = 'all')
       AND s.Amount >= @Min AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
       AND (@Like IS NULL OR s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\' OR s.Mobile LIKE @Like ESCAPE N'\'
                          OR s.UnitCode LIKE @Like ESCAPE N'\' OR s.VoucherNumber LIKE @Like ESCAPE N'\' OR s.TowerNumber LIKE @Like ESCAPE N'\' OR s.ProjectCode LIKE @Like ESCAPE N'\'
                          OR EXISTS (SELECT 1 FROM dbo.CollectionsTowers tq WHERE tq.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), tq.TowerNumber))) = s.TowerNumber
                                      AND CONVERT(nvarchar(400), tq.TowerName) LIKE @Like ESCAPE N'\')
                          OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%'))
     ORDER BY p.Oldest, p.CompanyId, p.TenantId, p.UnitCode, p.UnitId, s.DueDate, s.VoucherNumber COLLATE Latin1_General_BIN2, s.SnapshotRowId
     OPTION (RECOMPILE);

    EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope;
END;
GO
