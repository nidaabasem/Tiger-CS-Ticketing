/*
  R004 - Read procedures of the rebuilt receivables snapshot.  The application NEVER calls PACT for a page view; it reads these.

    dbo.usp_CollectionsRcv_GetUnits   RECEIVABLES page (and the unit list behind it): one row per apartment of a customer, its payments, filters, totals, paging
    dbo.usp_CollectionsRcv_GetLines   payment rows of a due-date window (used by Campaigns and the Review workflow, whose rules run in the application)
    dbo.usp_CollectionsRcv_GetUnit    Payment Summary: one unit by its normalised key (tower + unit code), never by a phone number
    dbo.usp_CollectionsRcv_GetTowers  active towers for the dropdown

  SEMANTICS (all dates are calendar days; @Today is today in Asia/Dubai, passed by the application)
    * Only unpaid or partly paid payments exist (remaining Amount > 0).  Overdue = due BEFORE @Today, Due = due ON @Today, anything later is NEVER read,
      listed, totalled or exported.  Total = Due + Overdue = the sum of the payments the details list.
    * @FromDate / @ToDate are the DUE-date window (NULL = no bound; @ToDate is capped at @Today).  A month filter passes that month's first and last day.
    * @Status: all (Due + Overdue, the default) | due | overdue - applied to the PAYMENTS, so every figure below is computed from the payments that match.
    * @MinTotal (NULL = none): applied AFTER the payments of one apartment are summed (Total > @MinTotal, strictly greater), before the counts, paging and export.
    * Excluded before aggregation and paging: unit code containing '*', UnitID <= 0, unit code blank or 0.  Only that apartment is dropped.
    * An apartment is (CompanyId, TenantId, UnitId, UnitCode), compared binary-exactly: other companies, customers and apartments are never merged.
    * The apartment's CONTACT (name / mobile / e-mail as PACT holds them) is that of its earliest matching payment (then smallest voucher, then id) - the same representative row Campaigns use,
      so the two pages never show different PACT contacts for one apartment.
    * ORDER: apartments by their latest matching due date DESC, then company, customer, unit code, unit id (a total order: pages never overlap or skip);
      payments by due date DESC, then voucher, then id.
    * Search: customer name, mobile, e-mail, customer id, tower number / name, apartment, voucher, project - PACT contact data AND the linked CRM contact.
  Result sets of usp_CollectionsRcv_GetUnits (a contract with RcvReceivablesSource):
    1 scope     ScopeValid, TowerId, CompanyId, TowerNumber, TowerName   (ScopeValid = 0: unknown tower or tower/company clash; nothing else is returned)
    2 status    usp_CollectionsRcv_GetStatus for the companies in scope
    3 totals    UnitCount, LineCount, DueLineCount, OverdueLineCount, DueTotal, OverdueTotal, Total   (over the whole filtered set, before paging)
    4 units     the requested page
    5 lines     the payments of exactly those units (sum per unit = its Total)
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetUnits
    @Today       date,
    @CompanyId   int           = NULL,
    @TowerId     int           = NULL,
    @FromDate    date          = NULL,
    @ToDate      date          = NULL,
    @Status      varchar(10)   = 'all',
    @Search      nvarchar(200) = NULL,
    @PhoneDigits nvarchar(200) = NULL,
    @MinTotal    decimal(19,4) = NULL,
    @PageNumber  int           = 1,
    @PageSize    int           = 25,
    @Only        tinyint       = NULL      -- NULL = all five result sets; 3, 4 or 5 = ONLY that one (so tools/verify_details_total.sql can INSERT ... EXEC it and compare the units with their payments)
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @Only IS NOT NULL AND @Only NOT IN (3, 4, 5) THROW 50036, N'@Only must be NULL, 3, 4 or 5.', 1;
    IF @Status NOT IN ('all', 'due', 'overdue') THROW 50035, N'Unknown status filter.', 1;
    IF @FromDate IS NOT NULL AND @ToDate IS NOT NULL AND @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @PageNumber < 1 OR @PageSize < 1 OR @PageSize > 100 THROW 50034, N'Invalid page.', 1;

    DECLARE @TowerFound bit = 0, @TowerCompany int, @TowerNumber nvarchar(20), @TowerName nvarchar(400);
    IF @TowerId IS NOT NULL
        SELECT @TowerFound = 1, @TowerCompany = t.CompanyId, @TowerNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))), @TowerName = CONVERT(nvarchar(400), t.TowerName)
          FROM dbo.CollectionsTowers t WHERE t.TowerId = @TowerId;
    DECLARE @Scope int = COALESCE(@TowerCompany, @CompanyId);
    DECLARE @ScopeValid bit = CASE WHEN @TowerId IS NULL THEN 1 WHEN @TowerFound = 1 AND (@CompanyId IS NULL OR @CompanyId = @TowerCompany) THEN 1 ELSE 0 END;
    IF @Only IS NULL SELECT @ScopeValid AS ScopeValid, @TowerId AS TowerId, @TowerCompany AS CompanyId, @TowerNumber AS TowerNumber, @TowerName AS TowerName;
    IF @ScopeValid = 0
    BEGIN
        IF @Only IS NULL
        BEGIN
            SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
            SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        END;
        RETURN;
    END;
    IF @Only IS NULL EXEC dbo.usp_CollectionsRcv_GetStatus @CompanyId = @Scope;

    DECLARE @Like nvarchar(420) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
        SET @Like = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';
    DECLARE @Last date = CASE WHEN @ToDate IS NULL OR @ToDate > @Today THEN @Today ELSE @ToDate END;   -- nothing due after today is ever read

    -- Towers whose NAME matches the search (a handful of rows), so the payment scan below does a keyed lookup instead of a correlated subquery per payment.
    DECLARE @TowerHits TABLE (CompanyId int NOT NULL, TowerNumber nvarchar(20) NOT NULL, PRIMARY KEY (CompanyId, TowerNumber));
    IF @Like IS NOT NULL
        INSERT @TowerHits SELECT DISTINCT tq.CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), tq.TowerNumber))) FROM dbo.CollectionsTowers tq
         WHERE CONVERT(nvarchar(400), tq.TowerName) LIKE @Like ESCAPE N'\';

    -- 1. The payments in play (everything except search and Minimum Total), narrow.
    CREATE TABLE #l
    (
        LineId bigint NOT NULL PRIMARY KEY, CompanyId int NOT NULL, RunId uniqueidentifier NOT NULL, TenantId nvarchar(100) COLLATE Latin1_General_BIN2 NOT NULL, UnitId bigint NOT NULL,
        UnitCode nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL, DueDate date NOT NULL, Amount decimal(19,4) NOT NULL, Hit bit NOT NULL
    );
    INSERT #l (LineId, CompanyId, RunId, TenantId, UnitId, UnitCode, DueDate, Amount, Hit)
    SELECT s.LineId, s.CompanyId, s.RunId, s.TenantId, s.UnitId, s.UnitCode, s.DueDate, s.Amount,
           CASE WHEN @Like IS NULL THEN 1
                WHEN s.FullName LIKE @Like ESCAPE N'\' OR s.TenantId COLLATE DATABASE_DEFAULT LIKE @Like ESCAPE N'\' OR s.Email LIKE @Like ESCAPE N'\' OR s.Mobile LIKE @Like ESCAPE N'\'
                  OR s.UnitCode COLLATE DATABASE_DEFAULT LIKE @Like ESCAPE N'\' OR s.VoucherNumber LIKE @Like ESCAPE N'\' OR s.TowerNumber LIKE @Like ESCAPE N'\' OR s.ProjectCode LIKE @Like ESCAPE N'\'
                  OR EXISTS (SELECT 1 FROM @TowerHits h WHERE h.CompanyId = s.CompanyId AND h.TowerNumber = s.TowerNumber)
                  OR (@PhoneDigits IS NOT NULL AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(s.Mobile, N' ', N''), N'+', N''), N'-', N''), N'(', N''), N')', N'') LIKE N'%' + @PhoneDigits + N'%')
                THEN 1 ELSE 0 END
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
     WHERE c.CurrentRunId IS NOT NULL AND (@Scope IS NULL OR c.CompanyId = @Scope)
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND s.DueDate <= @Last AND (@FromDate IS NULL OR s.DueDate >= @FromDate)
       AND (@Status = 'all' OR (@Status = 'due' AND s.DueDate = @Today) OR (@Status = 'overdue' AND s.DueDate < @Today))
       AND s.Amount > 0 AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
    OPTION (RECOMPILE);

    -- 2. One row per apartment.
    CREATE TABLE #u
    (
        CompanyId int NOT NULL, RunId uniqueidentifier NOT NULL, TenantId nvarchar(100) COLLATE Latin1_General_BIN2 NOT NULL, UnitId bigint NOT NULL, UnitCode nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
        Due decimal(19,4) NOT NULL, Overdue decimal(19,4) NOT NULL, Total decimal(19,4) NOT NULL, Cnt int NOT NULL, DueCnt int NOT NULL, OverCnt int NOT NULL, Latest date NOT NULL, Hit bit NOT NULL
    );
    INSERT #u
    SELECT CompanyId, RunId, TenantId, UnitId, UnitCode,
           SUM(CASE WHEN DueDate = @Today THEN Amount ELSE 0 END), SUM(CASE WHEN DueDate < @Today THEN Amount ELSE 0 END), SUM(Amount), COUNT(*),
           SUM(CASE WHEN DueDate = @Today THEN 1 ELSE 0 END), SUM(CASE WHEN DueDate < @Today THEN 1 ELSE 0 END), MAX(DueDate), MAX(CAST(Hit AS int))
      FROM #l GROUP BY CompanyId, RunId, TenantId, UnitId, UnitCode;

    -- Search also matches the linked CRM contact (CRM first, PACT completing): keep an apartment whose payments matched OR whose CRM customer does.
    IF @Like IS NOT NULL
    BEGIN
        SELECT l.CompanyId, l.UnitKey, l.FullName, l.PhoneNorm, l.EmailNorm INTO #crm FROM dbo.fn_Collections_CrmUnitLinks() l WHERE l.Customers = 1;
        DELETE u FROM #u u
         WHERE u.Hit = 0 AND NOT EXISTS (SELECT 1 FROM #crm c
                                          WHERE c.CompanyId = u.CompanyId AND c.UnitKey = dbo.fn_CollectionsUnitKey(u.UnitCode) COLLATE Latin1_General_BIN2
                                            AND (c.FullName LIKE @Like ESCAPE N'\' OR c.PhoneNorm LIKE @Like ESCAPE N'\' OR c.EmailNorm LIKE @Like ESCAPE N'\'
                                                 OR (@PhoneDigits IS NOT NULL AND REPLACE(c.PhoneNorm, N'+', N'') LIKE N'%' + @PhoneDigits + N'%')));
    END;
    -- Minimum Total: on the apartment's SUM, strictly greater, before totals, counts and paging.
    IF @MinTotal IS NOT NULL DELETE FROM #u WHERE Total <= @MinTotal;

    IF @Only IS NULL OR @Only = 3
    SELECT COUNT(*) AS UnitCount, ISNULL(SUM(Cnt), 0) AS LineCount, ISNULL(SUM(DueCnt), 0) AS DueLineCount, ISNULL(SUM(OverCnt), 0) AS OverdueLineCount,
           ISNULL(SUM(Due), 0) AS DueTotal, ISNULL(SUM(Overdue), 0) AS OverdueTotal, ISNULL(SUM(Total), 0) AS Total FROM #u;

    -- 3. The requested page (a total order).
    SELECT * INTO #p FROM
    (
        SELECT u.*, ROW_NUMBER() OVER (ORDER BY u.Latest DESC, u.CompanyId, u.TenantId, u.UnitCode, u.UnitId) AS Rn FROM #u u
    ) x WHERE x.Rn > (@PageNumber - 1) * @PageSize AND x.Rn <= @PageNumber * @PageSize;

    IF @Only IS NULL OR @Only = 4
    SELECT p.CompanyId, p.TenantId COLLATE DATABASE_DEFAULT AS TenantId, p.UnitId, p.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, r.FullName, r.TowerNumber,
           CONVERT(nvarchar(400), tw.TowerName) AS TowerName, r.ProjectCode, p.Due, p.Overdue, p.Total, p.Cnt AS InstalmentCount, p.Latest AS LatestDueDate,
           -- The CRM side of the apartment (V010; NULL = CRM holds no eligible sale or is not loaded): 1 customer -> that contact, > 1 -> ambiguous, nobody chosen.
           l.Customers AS CrmCustomers, CASE WHEN l.Customers = 1 THEN l.CustomerId END AS CrmCustomerId, CASE WHEN l.Customers = 1 THEN l.FullName END AS CrmName,
           CASE WHEN l.Customers = 1 THEN l.PhoneNorm END AS CrmPhone, CASE WHEN l.Customers = 1 THEN l.EmailNorm END AS CrmEmail
      FROM #p p
      CROSS APPLY (SELECT TOP (1) s.* FROM dbo.CollectionsRcvLine s
                    WHERE s.CompanyId = p.CompanyId AND s.RunId = p.RunId AND s.TenantId = p.TenantId AND s.UnitId = p.UnitId AND s.UnitCode = p.UnitCode
                    ORDER BY s.DueDate, s.VoucherNumber, s.LineId) r
      LEFT JOIN dbo.fn_Collections_CrmUnitLinks() l ON l.CompanyId = p.CompanyId AND l.UnitKey = dbo.fn_CollectionsUnitKey(p.UnitCode) COLLATE Latin1_General_BIN2
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = r.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = r.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     ORDER BY p.Rn;

    -- 4. Exactly the matching payments of those apartments (the same #l set, so the sum of a unit's rows is its Total).
    IF @Only IS NULL OR @Only = 5
    SELECT s.CompanyId, s.TenantId COLLATE DATABASE_DEFAULT AS TenantId, s.UnitId, s.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, s.VoucherNumber, s.ChequeNumber, s.DueDate,
           s.Amount AS RemainingAmount, s.Mobile, s.Email, s.SourceStatus,
           CASE WHEN s.DueDate < @Today THEN 'Overdue' ELSE 'Due' END AS Classification
      FROM #p p
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = p.CompanyId AND s.RunId = p.RunId AND s.TenantId = p.TenantId AND s.UnitId = p.UnitId
      JOIN #l l ON l.LineId = s.LineId
     WHERE s.UnitCode = p.UnitCode
     ORDER BY p.Rn, s.DueDate DESC, s.VoucherNumber COLLATE Latin1_General_BIN2, s.LineId
    OPTION (RECOMPILE);
END;
GO

/*
  Payment rows of a due-date window for the Campaigns evaluation and the Review workflow.  Same exclusions as above (unpaid only, never a cancelled / invalid
  apartment).  The window is the CALLER's: Campaigns pass a window that ends today, the Review workflow its month end.  @MinAmount keeps payments whose remaining
  amount is >= it (0 = every unpaid payment).
    1 scope   ScopeValid, TowerId, CompanyId, TowerNumber, TowerName
    2 status  usp_CollectionsRcv_GetStatus for the companies in scope
    3 lines   one row per payment
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetLines
    @CompanyId int           = NULL,
    @TowerId   int           = NULL,
    @FromDate  date          = NULL,
    @ToDate    date          = NULL,
    @MinAmount decimal(19,4) = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate IS NOT NULL AND @ToDate IS NOT NULL AND @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    DECLARE @TowerFound bit = 0, @TowerCompany int, @TowerNumber nvarchar(20), @TowerName nvarchar(400);
    IF @TowerId IS NOT NULL
        SELECT @TowerFound = 1, @TowerCompany = t.CompanyId, @TowerNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))), @TowerName = CONVERT(nvarchar(400), t.TowerName)
          FROM dbo.CollectionsTowers t WHERE t.TowerId = @TowerId;
    DECLARE @Scope int = COALESCE(@TowerCompany, @CompanyId);
    DECLARE @ScopeValid bit = CASE WHEN @TowerId IS NULL THEN 1 WHEN @TowerFound = 1 AND (@CompanyId IS NULL OR @CompanyId = @TowerCompany) THEN 1 ELSE 0 END;
    SELECT @ScopeValid AS ScopeValid, @TowerId AS TowerId, @TowerCompany AS CompanyId, @TowerNumber AS TowerNumber, @TowerName AS TowerName;
    IF @ScopeValid = 0
    BEGIN
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0; SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
        RETURN;
    END;
    EXEC dbo.usp_CollectionsRcv_GetStatus @CompanyId = @Scope;

    SELECT s.CompanyId, s.TenantId COLLATE DATABASE_DEFAULT AS TenantId, s.FullName, s.Mobile, s.Email, s.UnitId, s.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, s.ProjectCode,
           s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount, s.SourceStatus, s.TowerNumber, CONVERT(nvarchar(400), tw.TowerName) AS TowerName
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                    WHERE t.CompanyId = s.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = s.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     WHERE c.CurrentRunId IS NOT NULL AND (@Scope IS NULL OR c.CompanyId = @Scope)
       AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
       AND (@ToDate IS NULL OR s.DueDate <= @ToDate) AND (@FromDate IS NULL OR s.DueDate >= @FromDate)
       AND s.Amount > 0 AND s.Amount >= @MinAmount AND s.UnitId > 0 AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.UnitCode NOT LIKE N'%*%'
     ORDER BY s.CompanyId, s.TenantId, s.UnitId, s.DueDate, s.VoucherNumber, s.LineId   -- the order the evaluation takes its representative row from: earliest due date, then voucher
    OPTION (RECOMPILE);
END;
GO

/*
  Payment Summary by unit.  @UnitKey is the normalised key (TP140-101); @CompanyId narrows to one company (NULL = every company that holds the unit).
    1 status        usp_CollectionsRcv_GetStatus
    2 identities    per (company, tenant, unit): the PACT customer of the unit (name / mobile / e-mail as PACT holds them - mobile may be empty), tower, payment count
    3 instalments   ALL the unit's unpaid payments (future ones included), newest first; the application classifies them (@AsOfDate is kept for compatibility)
    4 crm           the CRM link of the unit (usp_Collections_GetCrmUnitLinks shape), all companies
  Rows with UnitID <= 0 or a cancelled ('*') / blank / 0 code never match.
*/
CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetUnit
    @UnitKey   nvarchar(220),
    @CompanyId int = NULL,
    @AsOfDate  date
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    DECLARE @Bare nvarchar(220) = SUBSTRING(@UnitKey, 3, 220);
    EXEC dbo.usp_CollectionsRcv_GetStatus @CompanyId = @CompanyId;

    SELECT s.CompanyId, s.RunId, s.TenantId, s.UnitId, s.UnitCode, COUNT(*) AS Cnt, MAX(s.DueDate) AS Latest
      INTO #k
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
     WHERE c.CurrentRunId IS NOT NULL AND (@CompanyId IS NULL OR c.CompanyId = @CompanyId)
       AND UPPER(LTRIM(RTRIM(s.UnitCode))) COLLATE Latin1_General_BIN2 IN (@UnitKey COLLATE Latin1_General_BIN2, @Bare COLLATE Latin1_General_BIN2)
       AND s.UnitId > 0 AND s.UnitCode NOT LIKE N'%*%' AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0') AND s.Amount > 0
     GROUP BY s.CompanyId, s.RunId, s.TenantId, s.UnitId, s.UnitCode
    OPTION (RECOMPILE);

    SELECT k.CompanyId, k.TenantId COLLATE DATABASE_DEFAULT AS TenantId, k.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, k.UnitId, r.FullName, r.Mobile, r.Email, r.TowerNumber,
           CONVERT(nvarchar(400), tw.TowerName) AS TowerName, k.Cnt AS AllRows, k.Cnt AS OpenRows
      FROM #k k
      CROSS APPLY (SELECT TOP (1) s.* FROM dbo.CollectionsRcvLine s
                    WHERE s.CompanyId = k.CompanyId AND s.RunId = k.RunId AND s.TenantId = k.TenantId AND s.UnitId = k.UnitId AND s.UnitCode = k.UnitCode
                    ORDER BY s.DueDate, s.VoucherNumber, s.LineId) r
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t WHERE t.CompanyId = r.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = r.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     ORDER BY k.CompanyId, k.TenantId, k.UnitCode, k.UnitId;

    SELECT s.CompanyId, s.TenantId COLLATE DATABASE_DEFAULT AS TenantId, s.UnitId, s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount AS RemainingAmount, s.SourceStatus
      FROM #k k
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = k.CompanyId AND s.RunId = k.RunId AND s.TenantId = k.TenantId AND s.UnitId = k.UnitId
     WHERE s.UnitCode = k.UnitCode AND s.Amount > 0   -- ALL unpaid instalments of the unit, future ones too: the Payment tab's Remaining Balance and its Not yet due rows; the application classifies by date
     ORDER BY k.CompanyId, k.TenantId, k.UnitId, s.DueDate DESC, s.VoucherNumber COLLATE Latin1_General_BIN2, s.LineId;

    SELECT l.CompanyId, l.UnitKey, l.Customers, l.CustomerId, l.CrmUnitId, l.LeadId, l.FullName, l.PhoneNorm, l.EmailNorm
      FROM dbo.fn_Collections_CrmUnitLinks() l
     WHERE l.UnitKey = @UnitKey COLLATE Latin1_General_BIN2 AND (@CompanyId IS NULL OR l.CompanyId = @CompanyId);
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_CollectionsRcv_GetTowers
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
