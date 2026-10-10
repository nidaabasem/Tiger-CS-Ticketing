/*
  V007 - Campaign engine: per-unit evaluation, review flags, totals and paging in SQL.

    dbo.usp_Collections_GetCampaignUnits   the Campaigns preview / export read. Filtering, per-unit aggregation, the stage amount rule,
                                           review flags, search, totals and OFFSET/FETCH paging run on the server; only the requested page
                                           (or, for an export, the - bounded - full result) of units reaches the application.

  Why this exists: the earlier read returned EVERY instalment of the date window (441 k rows in the synthetic benchmark) to the application,
  which grouped them per unit in memory. That cost seconds and hundreds of MB per request and got slower while a refresh was publishing.

  Result sets (order is a contract with SnapshotPactReceivablesSource.ReadCampaignAsync)
    1 scope      ScopeValid, TowerId, CompanyId, TowerNumber, TowerName
    2 coverage   usp_Collections_GetCoverage (same shape as every other read)
    3 header     Status, BadIdentityRows
                   Ok             totals and page follow
                   LegacyRequired the snapshot of a company in scope was not prepared for the SQL engine (published before V007, or text whose
                                  edge whitespace T-SQL cannot trim like .NET): the application evaluates the campaign itself. NEVER an empty list.
                   NeedsNorm      units of the result use phone / e-mail texts whose normalisation is not recorded yet; result set 7 lists the dictionary
                                  entries to normalise (ContactId, Kind, Raw). The application normalises them with its own code (usp_Collections_SetContactNorms),
                                  records the result on the units (usp_Collections_SyncUnitContacts) and calls again. The list can be empty when only the units lag.
    4 totals     TotalUnits, CleanUnits (no unit-level review reason), ReviewUnits (at least one unit-level reason)
    5 page       the requested units (see the SELECT)
    6 unmatched  usp_Collections_GetUnmatchedTowers
    7 missing    ContactId, Kind (1 phone, 2 e-mail), Raw

  Rules mirrored from the application (CollectionsCampaignPolicy / CollectionsCampaignAppService); the application keeps its in-memory
  implementation as the reference and the equivalence tests compare the two on the same snapshot:
    * Rows: window (due DAY in [@FromDate, @ToDate]) AND Amount > 0 AND Amount >= @MinAmount, current run of each company in scope.
    * Unit = (CompanyId, TenantId, UnitId, UnitCode); tenant and unit code are stored trimmed by the publish step. The publish step also gives every row
      its unit's dense integer key (UnitSeq, in campaign order) and the tenant's (TenantSeq), so this procedure groups and orders INTEGERS from the narrow
      covering index IX_CollectionsReceivableSnapshot_Campaign and reads the unit's static facts from the narrow dbo.CollectionsReceivableUnit (display text: ...UnitText).
    * Stage rows = the unit's rows whose due day is in [@StageFrom, @StageToExclusive). No stage rows -> not a candidate. Two stage rows on the
      same due day -> AmbiguousInstalments (amount NULL, still a candidate, needs review). Otherwise SUM(Amount) must be > @Threshold.
    * Review flags (bit mask): 1 Ambiguous, 2 AmountPrecision, 4 MissingUnitIdentity, 8 ConflictingContactDetails, 16 UnitAllocation,
      32 ContradictoryPaymentStatus, 64 NoValidContact. They are evaluated over ALL of the unit's window rows (not only the stage rows) and
      before paging, so counts and eligibility are exact over the whole result set. Source/global reasons (stale, coverage, currency, release
      gates) do not depend on the unit; the application adds them and derives Ready / NeedsReview / PreviewOnly / InternalReview.
    * The displayed contact is the unit's first window row: earliest due date, then voucher, then SnapshotRowId. Units whose rows all carry one identical
      (name, phone, e-mail, project) (Variants = 0) use the stored attributes; the rest (a small minority) are resolved from their rows exactly, and only
      those need the distinct-contact comparison behind ConflictingContactDetails.
    * Order: CompanyId, TenantId (binary), UnitCode (binary), UnitId - which is exactly UnitSeq order within a company.
    * Cancelled / invalid units: a unit whose UnitCode contains '*' (e.g. 513*), is blank or '0', or whose UnitID is not positive is excluded BEFORE any aggregation, flag,
      count, page or export; only that apartment (never the customer's other apartments).
    * Amounts (day-based revision): with @Today (Dubai date) every candidate carries DueAmount (rows due ON @Today), OverdueAmount (rows due BEFORE it) and their sum
      (the application's Total). The application also clamps @ToDate to @Today, so no future instalment is in any unit. A unit with no Due + Overdue is not a candidate;
      @MinTotal (NULL = no minimum) keeps units whose Due + Overdue is GREATER than it - applied to the unit's sum, before the counts, the paging and the export.
  Concurrency: like every read, only rows of CollectionsReceivableCompanyState.CurrentRunId are touched, by index range; no dirty-read hints.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCampaignUnits
    @TowerId          int           = NULL,
    @CompanyId        int           = NULL,
    @FromDate         date,
    @ToDate           date,
    @MinAmount        decimal(19,4) = 0,
    @StageFrom        date          = NULL,       -- inclusive lower due-day bound of the stage; NULL = unbounded
    @StageToExclusive date,                       -- exclusive upper due-day bound of the stage
    @Threshold        decimal(19,4),              -- a stage amount qualifies when SUM > @Threshold
    @ContactRequired  bit           = 1,          -- 0 for the internal legal-referral stage (no contact needed)
    @Search           nvarchar(200) = NULL,
    @PhoneDigits      nvarchar(200) = NULL,       -- digits of a phone-like search term (>= 3 digits, no letters), else NULL
    @Offset           int           = 0,
    @Take             int           = 25,
    @NormVersion      tinyint,
    @Today            date          = NULL,       -- the Dubai date: Due = due on it, Overdue = due before it
    @MinTotal         decimal(19,4) = NULL        -- unit Due + Overdue must be > this; NULL = no minimum
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    IF @FromDate > @ToDate THROW 50032, N'@FromDate must not be after @ToDate.', 1;
    IF @MinAmount < 0 THROW 50033, N'@MinAmount must not be negative.', 1;
    IF @Offset < 0 OR @Take < 1 OR @Take > 100000 THROW 50034, N'Invalid page.', 1;

    DECLARE @TowerFound bit = 0, @TowerCompany int, @TowerNumber nvarchar(20), @TowerName nvarchar(400);
    IF @TowerId IS NOT NULL
        SELECT @TowerFound = 1, @TowerCompany = t.CompanyId, @TowerNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))),
               @TowerName = CONVERT(nvarchar(400), t.TowerName)
          FROM dbo.CollectionsTowers t WHERE t.TowerId = @TowerId;
    DECLARE @Scope int = COALESCE(@TowerCompany, @CompanyId);
    DECLARE @ScopeValid bit = CASE WHEN @TowerId IS NULL THEN 1
                                   WHEN @TowerFound = 1 AND (@CompanyId IS NULL OR @CompanyId = @TowerCompany) THEN 1 ELSE 0 END;
    SELECT @ScopeValid AS ScopeValid, @TowerId AS TowerId, @TowerCompany AS CompanyId, @TowerNumber AS TowerNumber, @TowerName AS TowerName;

    -- Empty shapes for the early exits (result sets 3..7).
    DECLARE @EmptyOnly bit = 0;
    IF @ScopeValid = 0 SET @EmptyOnly = 1;
    IF @EmptyOnly = 0 EXEC dbo.usp_Collections_GetCoverage @Scope; ELSE SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;

    DECLARE @Status varchar(20) = 'Ok', @BadIdentity int = 0;
    IF @EmptyOnly = 0 AND EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState st
                                   WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL
                                     AND (st.ExoticTextRows IS NULL OR st.ExoticTextRows > 0))
        SET @Status = 'LegacyRequired';

    DECLARE @FromDt datetime = CAST(@FromDate AS datetime), @ToExclusive datetime = DATEADD(DAY, 1, CAST(@ToDate AS datetime));
    DECLARE @Like nvarchar(420) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
        SET @Like = N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';

    CREATE TABLE #run (CompanyId int NOT NULL PRIMARY KEY, RunId uniqueidentifier NOT NULL);
    CREATE TABLE #u   -- (unit, due day): the narrow aggregate every later step works from (integers only)
    (
        CompanyId int NOT NULL, UnitSeq int NOT NULL, TenantSeq int NOT NULL, Day_ date NOT NULL,
        Cnt int NOT NULL, Amt decimal(19,4) NOT NULL, Contra int NOT NULL
    );
    CREATE TABLE #g   -- per unit (integers only)
    (
        CompanyId int NOT NULL, UnitSeq int NOT NULL, TenantSeq int NOT NULL,
        StageRows int NOT NULL, StageAmt decimal(19,4) NOT NULL, StageMin date NULL, StageAmb int NOT NULL, Contra int NOT NULL,
        DueAmt decimal(19,4) NOT NULL, OverAmt decimal(19,4) NOT NULL
    );
    CREATE TABLE #a   -- candidate units
    (
        CompanyId int NOT NULL, UnitSeq int NOT NULL, TenantSeq int NOT NULL, StageAmt decimal(19,4) NOT NULL, StageMin date NULL, StageAmb int NOT NULL, Contra int NOT NULL,
        DueAmt decimal(19,4) NOT NULL, OverAmt decimal(19,4) NOT NULL,
        CodeSeq int NOT NULL, UnitId bigint NOT NULL, Variants bit NOT NULL, BadCode bit NOT NULL, PhoneId int NULL, EmailId int NULL, PhoneOk bit NULL, EmailOk bit NULL,
        PhoneVer tinyint NULL, EmailVer tinyint NULL, Conflict int NOT NULL DEFAULT 0, UnitAlloc int NOT NULL DEFAULT 0, Flags int NULL,
        PRIMARY KEY (CompanyId, UnitSeq)
    );
    CREATE TABLE #vo (CompanyId int NOT NULL, UnitSeq int NOT NULL, FullName nvarchar(400) NOT NULL, ProjectCode nvarchar(200) NOT NULL, TowerNumber nvarchar(20) NULL,
                      PRIMARY KEY (CompanyId, UnitSeq));   -- first-row attributes of the Variants units
    CREATE TABLE #miss (ContactId int NOT NULL PRIMARY KEY, Kind tinyint NOT NULL, Raw nvarchar(400) COLLATE Latin1_General_BIN2 NOT NULL);

    IF @EmptyOnly = 0 AND @Status = 'Ok'
    BEGIN
        INSERT #run SELECT st.CompanyId, st.CurrentRunId FROM dbo.CollectionsReceivableCompanyState st
                     WHERE (@Scope IS NULL OR st.CompanyId = @Scope) AND st.CurrentRunId IS NOT NULL;

        INSERT #u WITH (TABLOCK)
        SELECT s.CompanyId, s.UnitSeq, s.TenantSeq, CAST(s.DueDate AS date), COUNT(*), SUM(s.Amount), MAX(s.RowFlags & 1)
          FROM #run r
          JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = r.CompanyId AND s.RunId = r.RunId
          JOIN dbo.CollectionsReceivableUnitText ut ON ut.CompanyId = s.CompanyId AND ut.RunId = s.RunId AND ut.UnitSeq = s.UnitSeq
         WHERE s.DueDate >= @FromDt AND s.DueDate < @ToExclusive AND s.Amount > 0 AND s.Amount >= @MinAmount
           AND ut.UnitCode NOT LIKE N'%*%'                                  -- cancelled apartments (e.g. 513*) never enter the aggregation
           AND LTRIM(RTRIM(ut.UnitCode)) NOT IN (N'', N'0') AND s.UnitId > 0 -- nor do units without a real number
           AND (@TowerId IS NULL OR s.TowerNumber = @TowerNumber)
         GROUP BY s.CompanyId, s.UnitSeq, s.TenantSeq, CAST(s.DueDate AS date)
        OPTION (RECOMPILE);

        INSERT #g WITH (TABLOCK)
        SELECT u.CompanyId, u.UnitSeq, MIN(u.TenantSeq),
               SUM(CASE WHEN st.InStage = 1 THEN u.Cnt ELSE 0 END), SUM(CASE WHEN st.InStage = 1 THEN u.Amt ELSE 0 END),
               MIN(CASE WHEN st.InStage = 1 THEN u.Day_ END), MAX(CASE WHEN st.InStage = 1 AND u.Cnt > 1 THEN 1 ELSE 0 END), MAX(u.Contra),
               SUM(CASE WHEN @Today IS NOT NULL AND u.Day_ = @Today THEN u.Amt ELSE 0 END), SUM(CASE WHEN @Today IS NOT NULL AND u.Day_ < @Today THEN u.Amt ELSE 0 END)
          FROM #u u
         CROSS APPLY (SELECT CASE WHEN (@StageFrom IS NULL OR u.Day_ >= @StageFrom) AND u.Day_ < @StageToExclusive THEN 1 ELSE 0 END AS InStage) st
         GROUP BY u.CompanyId, u.UnitSeq;

        -- Candidates: at least one stage row, and either ambiguous (review) or above the stage threshold.
        INSERT #a WITH (TABLOCK) (CompanyId, UnitSeq, TenantSeq, StageAmt, StageMin, StageAmb, Contra, DueAmt, OverAmt, CodeSeq, UnitId, Variants, BadCode, PhoneId, EmailId, PhoneOk, EmailOk, PhoneVer, EmailVer)
        SELECT g.CompanyId, g.UnitSeq, g.TenantSeq, g.StageAmt, g.StageMin, g.StageAmb, g.Contra, g.DueAmt, g.OverAmt,
               d.CodeSeq, d.UnitId, d.Variants, d.BadCode, d.PhoneId, d.EmailId, d.PhoneOk, d.EmailOk, d.PhoneVer, d.EmailVer
          FROM #g g
          JOIN #run r ON r.CompanyId = g.CompanyId
          JOIN dbo.CollectionsReceivableUnit d ON d.CompanyId = g.CompanyId AND d.RunId = r.RunId AND d.UnitSeq = g.UnitSeq
         WHERE g.StageRows > 0 AND (g.StageAmb = 1 OR g.StageAmt > @Threshold)
           AND (@Today IS NULL OR g.DueAmt + g.OverAmt > 0)                 -- nothing Due or Overdue: not listed
           AND (@MinTotal IS NULL OR g.DueAmt + g.OverAmt > @MinTotal)      -- Minimum Total: on the unit's sum, before counts / paging / export
         ORDER BY g.CompanyId, g.UnitSeq;

        -- Units whose rows carry different contact details: resolve the first window row (DueDate, VoucherNumber, SnapshotRowId) and the distinct
        -- (name, phone, e-mail, project) tuples exactly, from their own rows (a few index seeks per unit).
        CREATE TABLE #vt (CompanyId int NOT NULL, UnitSeq int NOT NULL, NameT nvarchar(400) COLLATE Latin1_General_BIN2 NOT NULL,
                          PhoneId int NULL, EmailId int NULL, Prj nvarchar(202) COLLATE Latin1_General_BIN2 NOT NULL);
        CREATE TABLE #vf (CompanyId int NOT NULL, UnitSeq int NOT NULL, PhoneId int NULL, EmailId int NULL);
        IF EXISTS (SELECT 1 FROM #a WHERE Variants = 1)
        BEGIN
            SELECT a.CompanyId, a.UnitSeq, s.SnapshotRowId, s.DueDate, s.VoucherNumber, s.FullName, s.Mobile, s.Email, s.ProjectCode, s.TowerNumber
              INTO #vr
              FROM #a a
              JOIN #run r ON r.CompanyId = a.CompanyId
              JOIN dbo.CollectionsReceivableUnitText t ON t.CompanyId = a.CompanyId AND t.RunId = r.RunId AND t.UnitSeq = a.UnitSeq
              JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = a.CompanyId AND s.RunId = r.RunId
                                                       AND s.TenantId = t.TenantId COLLATE DATABASE_DEFAULT AND s.UnitId = a.UnitId AND s.UnitSeq = a.UnitSeq
             WHERE a.Variants = 1 AND s.DueDate >= @FromDt AND s.DueDate < @ToExclusive AND s.Amount > 0 AND s.Amount >= @MinAmount;

            INSERT #vf
            SELECT f.CompanyId, f.UnitSeq, CASE WHEN f.Mobile = N'' THEN NULL ELSE p.ContactId END, CASE WHEN f.Email = N'' THEN NULL ELSE e.ContactId END
              FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY CompanyId, UnitSeq ORDER BY DueDate, VoucherNumber, SnapshotRowId) AS rn FROM #vr) f
              LEFT JOIN dbo.CollectionsContactNorm p ON p.Kind = 1 AND p.Raw = f.Mobile COLLATE Latin1_General_BIN2
              LEFT JOIN dbo.CollectionsContactNorm e ON e.Kind = 2 AND e.Raw = f.Email COLLATE Latin1_General_BIN2
             WHERE f.rn = 1;
            INSERT #vo SELECT f.CompanyId, f.UnitSeq, f.FullName, f.ProjectCode, f.TowerNumber
              FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY CompanyId, UnitSeq ORDER BY DueDate, VoucherNumber, SnapshotRowId) AS rn FROM #vr) f WHERE f.rn = 1;

            INSERT #vt
            SELECT v.CompanyId, v.UnitSeq, v.NameT, p.ContactId, e.ContactId, v.Prj
              FROM (SELECT DISTINCT CompanyId, UnitSeq, LTRIM(RTRIM(FullName)) COLLATE Latin1_General_BIN2 AS NameT, Mobile COLLATE Latin1_General_BIN2 AS Mob,
                           Email COLLATE Latin1_General_BIN2 AS Eml, (ProjectCode + N'|') COLLATE Latin1_General_BIN2 AS Prj FROM #vr) v
              LEFT JOIN dbo.CollectionsContactNorm p ON p.Kind = 1 AND v.Mob <> N'' AND p.Raw = v.Mob
              LEFT JOIN dbo.CollectionsContactNorm e ON e.Kind = 2 AND v.Eml <> N'' AND e.Raw = v.Eml;

            -- The displayed contact of a Variants unit is its first row's: take its ids and validity from the dictionary.
            UPDATE a SET PhoneId = f.PhoneId, EmailId = f.EmailId,
                         PhoneOk = CASE WHEN f.PhoneId IS NULL THEN CAST(0 AS bit) WHEN pn.NormVersion = @NormVersion THEN CASE WHEN pn.Norm = N'' THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END END,
                         EmailOk = CASE WHEN f.EmailId IS NULL THEN CAST(0 AS bit) WHEN en.NormVersion = @NormVersion THEN CASE WHEN en.Norm = N'' THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END END,
                         PhoneVer = CASE WHEN f.PhoneId IS NULL THEN NULL ELSE @NormVersion END, EmailVer = CASE WHEN f.EmailId IS NULL THEN NULL ELSE @NormVersion END
              FROM #a a
              JOIN #vf f ON f.CompanyId = a.CompanyId AND f.UnitSeq = a.UnitSeq
              LEFT JOIN dbo.CollectionsContactNorm pn ON pn.ContactId = f.PhoneId
              LEFT JOIN dbo.CollectionsContactNorm en ON en.ContactId = f.EmailId;
        END;

        -- Pending normalisations: a candidate whose phone / e-mail validity is not recorded for this version. The dictionary entries still to normalise are
        -- listed; units that merely lag behind an already normalised dictionary are fixed by usp_Collections_SyncUnitContacts (the list is then empty).
        IF EXISTS (SELECT 1 FROM #a WHERE (PhoneId IS NOT NULL AND (PhoneOk IS NULL OR PhoneVer IS NULL OR PhoneVer <> @NormVersion))
                                        OR (EmailId IS NOT NULL AND (EmailOk IS NULL OR EmailVer IS NULL OR EmailVer <> @NormVersion)))
           OR EXISTS (SELECT 1 FROM #vt WHERE PhoneId IS NOT NULL OR EmailId IS NOT NULL)
        BEGIN
            INSERT #miss (ContactId, Kind, Raw)
            SELECT n.ContactId, n.Kind, n.Raw
              FROM dbo.CollectionsContactNorm n
             WHERE (n.Norm IS NULL OR n.NormVersion IS NULL OR n.NormVersion <> @NormVersion)
               AND n.ContactId IN (SELECT PhoneId FROM #a WHERE PhoneOk IS NULL OR PhoneVer IS NULL OR PhoneVer <> @NormVersion
                                   UNION SELECT EmailId FROM #a WHERE EmailOk IS NULL OR EmailVer IS NULL OR EmailVer <> @NormVersion
                                   UNION SELECT PhoneId FROM #vt UNION SELECT EmailId FROM #vt);
            IF EXISTS (SELECT 1 FROM #miss)
               OR EXISTS (SELECT 1 FROM #a WHERE (PhoneId IS NOT NULL AND (PhoneOk IS NULL OR PhoneVer IS NULL OR PhoneVer <> @NormVersion))
                                              OR (EmailId IS NOT NULL AND (EmailOk IS NULL OR EmailVer IS NULL OR EmailVer <> @NormVersion)))
                SET @Status = 'NeedsNorm';
        END;

        IF @Status = 'Ok'
        BEGIN
            -- ConflictingContactDetails: more than one distinct (name, normalised phone, normalised e-mail, project) among the unit's window rows.
            IF EXISTS (SELECT 1 FROM #vt)
                UPDATE a SET Conflict = 1
                  FROM #a a
                  JOIN (SELECT t.CompanyId, t.UnitSeq
                          FROM #vt t
                          LEFT JOIN dbo.CollectionsContactNorm pn ON pn.ContactId = t.PhoneId
                          LEFT JOIN dbo.CollectionsContactNorm en ON en.ContactId = t.EmailId
                         GROUP BY t.CompanyId, t.UnitSeq
                        HAVING COUNT(DISTINCT CONCAT(t.NameT, NCHAR(1), ISNULL(pn.Norm, N''), NCHAR(1), ISNULL(en.Norm, N''), NCHAR(1), t.Prj) COLLATE Latin1_General_BIN2) > 1) c
                    ON c.CompanyId = a.CompanyId AND c.UnitSeq = a.UnitSeq;

            -- UnitAllocationNeedsReview: tenant-level, over ALL of the tenant's window rows. A tenant is ambiguous when one due day carries more than one
            -- distinct unit, one unit id has more than one code, or one code has more than one unit id. Only tenants with more than one unit in the window
            -- can be ambiguous, so only their rows are looked at.
            SELECT g.CompanyId, g.TenantSeq INTO #mt FROM #g g GROUP BY g.CompanyId, g.TenantSeq HAVING COUNT(*) > 1;
            IF EXISTS (SELECT 1 FROM #mt)
            BEGIN
                SELECT g.CompanyId, g.TenantSeq, g.UnitSeq, d.UnitId, d.CodeSeq INTO #mg
                  FROM #g g JOIN #mt m ON m.CompanyId = g.CompanyId AND m.TenantSeq = g.TenantSeq
                  JOIN #run r ON r.CompanyId = g.CompanyId
                  JOIN dbo.CollectionsReceivableUnit d ON d.CompanyId = g.CompanyId AND d.RunId = r.RunId AND d.UnitSeq = g.UnitSeq;
                UPDATE a SET UnitAlloc = 1 FROM #a a
                 WHERE EXISTS (SELECT 1 FROM #u u JOIN #mt m ON m.CompanyId = u.CompanyId AND m.TenantSeq = u.TenantSeq
                                WHERE u.CompanyId = a.CompanyId AND u.TenantSeq = a.TenantSeq GROUP BY u.Day_ HAVING COUNT(*) > 1)
                    OR EXISTS (SELECT 1 FROM #mg g WHERE g.CompanyId = a.CompanyId AND g.TenantSeq = a.TenantSeq GROUP BY g.UnitId HAVING COUNT(*) > 1)
                    OR EXISTS (SELECT 1 FROM #mg g WHERE g.CompanyId = a.CompanyId AND g.TenantSeq = a.TenantSeq GROUP BY g.CodeSeq HAVING COUNT(*) > 1);
            END;

            UPDATE #a SET Flags =
                   (CASE WHEN StageAmb = 1 THEN 1 ELSE 0 END)
                 | (CASE WHEN StageAmb = 0 AND StageAmt % 0.01 <> 0 THEN 2 ELSE 0 END)
                 | (CASE WHEN BadCode = 1 THEN 4 ELSE 0 END)
                 | (CASE WHEN Conflict = 1 THEN 8 ELSE 0 END)
                 | (CASE WHEN UnitAlloc = 1 THEN 16 ELSE 0 END)
                 | (CASE WHEN Contra = 1 THEN 32 ELSE 0 END)
                 | (CASE WHEN @ContactRequired = 1 AND ISNULL(PhoneOk, 0) = 0 AND ISNULL(EmailOk, 0) = 0 THEN 64 ELSE 0 END);

            IF @Like IS NOT NULL
                DELETE a FROM #a a
                  JOIN #run r ON r.CompanyId = a.CompanyId
                  JOIN dbo.CollectionsReceivableUnitText d ON d.CompanyId = a.CompanyId AND d.RunId = r.RunId AND d.UnitSeq = a.UnitSeq
                  LEFT JOIN #vo vo ON vo.CompanyId = a.CompanyId AND vo.UnitSeq = a.UnitSeq
                  LEFT JOIN dbo.CollectionsContactNorm pn ON pn.ContactId = a.PhoneId
                  LEFT JOIN dbo.CollectionsContactNorm en ON en.ContactId = a.EmailId
                 WHERE NOT (   ISNULL(vo.FullName, d.FullName) LIKE @Like ESCAPE N'\' OR d.TenantId COLLATE DATABASE_DEFAULT LIKE @Like ESCAPE N'\'
                            OR d.UnitCode COLLATE DATABASE_DEFAULT LIKE @Like ESCAPE N'\' OR ISNULL(pn.Norm, N'') LIKE @Like ESCAPE N'\' OR ISNULL(en.Norm, N'') LIKE @Like ESCAPE N'\'
                            OR ISNULL(vo.TowerNumber, d.TowerNumber) LIKE @Like ESCAPE N'\'
                            OR EXISTS (SELECT 1 FROM dbo.CollectionsTowers tq WHERE tq.CompanyId = a.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), tq.TowerNumber))) = ISNULL(vo.TowerNumber, d.TowerNumber)
                                        AND CONVERT(nvarchar(400), tq.TowerName) LIKE @Like ESCAPE N'\')
                            OR (@PhoneDigits IS NOT NULL AND ISNULL(pn.Norm, N'') LIKE N'%' + @PhoneDigits + N'%'));
        END;
    END;

    SELECT @Status AS Status, @BadIdentity AS BadIdentityRows;

    IF @Status = 'Ok' AND @EmptyOnly = 0
    BEGIN
        SELECT COUNT(*) AS TotalUnits, ISNULL(SUM(CASE WHEN Flags = 0 THEN 1 ELSE 0 END), 0) AS CleanUnits,
               ISNULL(SUM(CASE WHEN Flags <> 0 THEN 1 ELSE 0 END), 0) AS ReviewUnits
          FROM #a;

        SELECT p.CompanyId, d.TenantId COLLATE DATABASE_DEFAULT AS TenantId, ISNULL(vo.FullName, d.FullName) AS FullName, ISNULL(pn.Norm, N'') AS Phone,
               ISNULL(en.Norm, N'') AS Email, p.UnitId, d.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, ISNULL(vo.ProjectCode, d.ProjectCode) AS ProjectCode,
               CASE WHEN p.StageAmb = 1 THEN CAST(NULL AS decimal(19,4)) ELSE p.StageAmt END AS Amount, p.StageMin AS EarliestDue, p.Flags, p.DueAmt AS DueAmount, p.OverAmt AS OverdueAmount,
               CASE WHEN vo.UnitSeq IS NULL THEN d.TowerNumber ELSE vo.TowerNumber END AS TowerNumber, CONVERT(nvarchar(400), tw.TowerName) AS TowerName
          FROM (SELECT * FROM #a ORDER BY CompanyId, UnitSeq OFFSET @Offset ROWS FETCH NEXT @Take ROWS ONLY) p
          JOIN #run r ON r.CompanyId = p.CompanyId
          JOIN dbo.CollectionsReceivableUnitText d ON d.CompanyId = p.CompanyId AND d.RunId = r.RunId AND d.UnitSeq = p.UnitSeq
          LEFT JOIN #vo vo ON vo.CompanyId = p.CompanyId AND vo.UnitSeq = p.UnitSeq
          LEFT JOIN dbo.CollectionsContactNorm pn ON pn.ContactId = p.PhoneId
          LEFT JOIN dbo.CollectionsContactNorm en ON en.ContactId = p.EmailId
          OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t
                        WHERE t.CompanyId = p.CompanyId
                          AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = CASE WHEN vo.UnitSeq IS NULL THEN d.TowerNumber ELSE vo.TowerNumber END
                        ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
         ORDER BY p.CompanyId, p.UnitSeq;
    END
    ELSE
    BEGIN
        SELECT CAST(0 AS int) AS TotalUnits, CAST(0 AS int) AS CleanUnits, CAST(0 AS int) AS ReviewUnits WHERE 1 = 0;
        SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
    END;

    IF @EmptyOnly = 0 EXEC dbo.usp_Collections_GetUnmatchedTowers @Scope; ELSE SELECT CAST(NULL AS int) AS CompanyId WHERE 1 = 0;
    SELECT ContactId, Kind, Raw FROM #miss WHERE @Status = 'NeedsNorm';
END;
GO

/* Stores the application's normalisations: @Json = [{"id":123,"norm":"+971500000000"}, ...]. At most ~2000 entries per call keep the statement below lock
   escalation (the application splits larger lists). Units are brought up to date by usp_Collections_SyncUnitContacts. */
CREATE OR ALTER PROCEDURE dbo.usp_Collections_SetContactNorms @NormVersion tinyint, @Json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE n SET Norm = j.Norm, NormVersion = @NormVersion
      FROM OPENJSON(@Json) WITH (Id int '$.id', Norm nvarchar(400) '$.norm') j
      JOIN dbo.CollectionsContactNorm n ON n.ContactId = j.Id;
    SELECT @@ROWCOUNT AS Updated;
END;
GO

/* Records, on the units of every retained run, whether their phone / e-mail normalised to a valid value (from dbo.CollectionsContactNorm entries of the
   given version). Small committed batches over UnitSeq ranges, so it never holds a table lock against readers. Idempotent; returns the number of units updated. */
CREATE OR ALTER PROCEDURE dbo.usp_Collections_SyncUnitContacts @NormVersion tinyint
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @total int = 0, @n int, @lo int, @hi int, @batch int = 1500, @company int, @run uniqueidentifier;
    DECLARE runs CURSOR LOCAL FAST_FORWARD FOR
        SELECT CompanyId, RunId, MIN(UnitSeq), MAX(UnitSeq) FROM dbo.CollectionsReceivableUnit GROUP BY CompanyId, RunId;
    OPEN runs;
    FETCH NEXT FROM runs INTO @company, @run, @lo, @hi;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        WHILE @lo <= @hi
        BEGIN
            -- Each side is only written when its dictionary entry is available (otherwise it keeps its current, pending, state).
            UPDATE u SET PhoneOk = CASE WHEN u.PhoneId IS NULL THEN CAST(0 AS bit) WHEN pn.Norm IS NULL THEN u.PhoneOk WHEN pn.Norm = N'' THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END,
                         PhoneVer = CASE WHEN u.PhoneId IS NULL THEN NULL WHEN pn.Norm IS NULL THEN u.PhoneVer ELSE @NormVersion END,
                         EmailOk = CASE WHEN u.EmailId IS NULL THEN CAST(0 AS bit) WHEN en.Norm IS NULL THEN u.EmailOk WHEN en.Norm = N'' THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END,
                         EmailVer = CASE WHEN u.EmailId IS NULL THEN NULL WHEN en.Norm IS NULL THEN u.EmailVer ELSE @NormVersion END
              FROM dbo.CollectionsReceivableUnit u
              LEFT JOIN dbo.CollectionsContactNorm pn ON pn.ContactId = u.PhoneId AND pn.NormVersion = @NormVersion
              LEFT JOIN dbo.CollectionsContactNorm en ON en.ContactId = u.EmailId AND en.NormVersion = @NormVersion
             WHERE u.CompanyId = @company AND u.RunId = @run AND u.UnitSeq >= @lo AND u.UnitSeq < @lo + @batch
               AND ((u.PhoneId IS NOT NULL AND pn.Norm IS NOT NULL AND (u.PhoneOk IS NULL OR u.PhoneVer IS NULL OR u.PhoneVer <> @NormVersion))
                 OR (u.EmailId IS NOT NULL AND en.Norm IS NOT NULL AND (u.EmailOk IS NULL OR u.EmailVer IS NULL OR u.EmailVer <> @NormVersion)));
            SET @total += @@ROWCOUNT;
            SET @lo += @batch;
        END;
        FETCH NEXT FROM runs INTO @company, @run, @lo, @hi;
    END;
    CLOSE runs; DEALLOCATE runs;
    SELECT @total AS Updated;
END;
GO
