/*
  V009 - Receivables month overview: dbo.usp_Collections_GetInstalmentMonths

  One row per due-date month over the WHOLE filtered set (tower, due-date window, payment status, minimum outstanding amount, search), before any paging and
  without any month selection, so the month cards always show every month of the current filters. The predicates are the same as
  dbo.usp_Collections_GetInstalmentsPage / GetInstalmentUnitsPage, and Overdue uses the same rule (Amount > 0 AND DueDate < first day of @AsOfDate's month),
  so the cards, the list and the totals can never disagree: a month's InstalmentCount / RemainingTotal equal the totals of the list filtered to that month.

  Result sets
    1 scope     ScopeValid, TowerId, CompanyId, TowerNumber, TowerName
    2 months    DueYear, DueMonth, InstalmentCount, RemainingTotal, OverdueCount, OverdueRemaining (oldest month first)
  Idempotent (CREATE OR ALTER); no table changes; needs V002-V006.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetInstalmentMonths
    @TowerId       int           = NULL,
    @CompanyId     int           = NULL,
    @FromDate      date,
    @ToDate        date,
    @AsOfDate      date,
    @MinAmount     decimal(19,4) = 0,
    @PaymentFilter varchar(12)   = 'outstanding',   -- outstanding | unpaid | partial | paid | all
    @Search        nvarchar(200) = NULL,
    @PhoneDigits   nvarchar(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @PaymentFilter NOT IN ('outstanding', 'unpaid', 'partial', 'paid', 'all') THROW 50030, N'Unknown payment filter.', 1;
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
        SELECT CAST(NULL AS int) AS DueYear WHERE 1 = 0;
        RETURN;
    END;

    DECLARE @Unavailable bit = 0;
    IF @PaymentFilter IN ('paid', 'all') AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE (@Scope IS NULL OR CompanyId = @Scope) AND CurrentRunId IS NOT NULL AND PaidRetained = 0)
        SET @Unavailable = 1;
    IF @PaymentFilter IN ('unpaid', 'partial') AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE (@Scope IS NULL OR CompanyId = @Scope) AND CurrentRunId IS NOT NULL AND BreakdownAvailable = 0)
        SET @Unavailable = 1;
    IF @Unavailable = 1
    BEGIN
        SELECT CAST(NULL AS int) AS DueYear WHERE 1 = 0;
        RETURN;
    END;

    DECLARE @MonthStart date = DATEFROMPARTS(YEAR(@AsOfDate), MONTH(@AsOfDate), 1);
    DECLARE @MonthStartDt datetime = CAST(@MonthStart AS datetime);
    DECLARE @ToExclusive datetime = DATEADD(DAY, 1, CAST(@ToDate AS datetime));
    DECLARE @FromDt datetime = CAST(@FromDate AS datetime);
    DECLARE @Min decimal(19,4) = CASE WHEN @PaymentFilter IN ('paid', 'all') THEN 0 ELSE @MinAmount END;
    DECLARE @Like nvarchar(420) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
        SET @Like = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';

    SELECT YEAR(s.DueDate) AS DueYear, MONTH(s.DueDate) AS DueMonth,
           COUNT(*) AS InstalmentCount, ISNULL(SUM(s.Amount), 0) AS RemainingTotal,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate < @MonthStartDt THEN 1 ELSE 0 END), 0) AS OverdueCount,
           ISNULL(SUM(CASE WHEN s.Amount > 0 AND s.DueDate < @MonthStartDt THEN s.Amount ELSE 0 END), 0) AS OverdueRemaining
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
     GROUP BY YEAR(s.DueDate), MONTH(s.DueDate)
     ORDER BY DueYear, DueMonth
    OPTION (RECOMPILE);
END;
GO
