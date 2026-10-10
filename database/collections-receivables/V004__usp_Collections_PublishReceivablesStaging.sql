/*
  V004 - Validate and publish the staged rows of ONE company, or record a failure and keep the previous snapshot.

  dbo.usp_Collections_RecordReceivablesFailure   records a failed attempt for a company (previous snapshot untouched)
  dbo.usp_Collections_CloseReceivablesRun        gives every started run/company a terminal state when its session died (timeout, cancellation, restart)
  dbo.usp_Collections_PublishReceivablesStaging  validates dbo.CollectionsReceivableStaging, then publishes it

  Validation (any failure => nothing is published and the previous snapshot stays current):
    * zero staged rows                       - an empty PACT result is treated as a failure, never as "no receivables"
    * more than @MaxRawRows rows             - protects the database from a runaway result
    * CompanyID <> expected / NULL           - wrong source
    * NULL DueDate or Amount                 - unusable rows
    * negative Amount                        - the procedures return NewFutureAmount >= 0; a negative value means the meaning changed
    * published rows < (100 - @MaxShrinkPercent)% of the previous snapshot (previous >= @ShrinkGuardMinRows),
      unless @AllowLargeShrink = 1 (operator override)  - guards against a silently truncated source
  Row rules:
    * Amount = 0 (paid / settled, incl. float residue rounded to 4 dp)   -> RETAINED as PaymentStatus FullyPaid when @RetainPaid = 1 (default; lets the UI offer
                                                                           "Fully paid" and "All"), otherwise excluded and counted
    * Amount > 0 and UnitID NULL or <= 0                                  -> excluded, counted with its amount (reported, never silent)
    * Amount > 0 and blank TenantID                                       -> excluded, counted with its amount
    * Status 'Paid' with Amount > 0 (contradiction) / status other than Paid|Installment -> KEPT, counted for review
  Payment status (stored per row, never inferred from dates or contract values):
    * companion shape (@BreakdownPresent = 1: PlanAmount and AllocatedAmount returned): Plan - Allocated must equal the remaining Amount (+-0.01), else Unknown.
      Remaining = 0 -> FullyPaid; Allocated > 0 -> PartiallyPaid; otherwise Unpaid. OriginalAmount / PaidAmount are the returned values.
    * deployed shape (Amount + Status only): Amount = 0 with Status Paid -> FullyPaid; every positive remainder -> Unknown ("needs verification"), because
      Installment does not distinguish unpaid from partially paid and the original / paid amounts are not returned.
  Amount meaning (verified in docs/Collections/pact-sql/Receivables-Source-Review.md): the REMAINING unpaid amount of the
  instalment. Status Paid <=> remaining 0; 'Installment' does not distinguish unpaid from partially paid.
  Duplicate rows are preserved (the source fan-out cannot be told from legitimate repeats; the application flags them).
  Campaign preparation (V007), done BEFORE the pointer flip and in small committed batches like the rows themselves:
    * every published row gets UnitSeq / TenantSeq (dense integer keys, binary collation) and RowFlags; UnitSeq follows the campaign order
    * dbo.CollectionsReceivableUnit / ...UnitText get one row per unit of the run ("contact details vary", phone / e-mail ids and validity; display text)
    * every distinct phone / e-mail text is registered in dbo.CollectionsContactNorm (Norm NULL = the application normalises it on first use)
    * ExoticTextRows records whether the SQL campaign engine may be used for the run (see V002)
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_RecordReceivablesFailure
    @RunId uniqueidentifier, @CompanyId int, @ErrorNumber int, @ErrorMessage nvarchar(4000),
    @RawRows int = NULL, @ShapeVariant tinyint = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @msg nvarchar(1000) = LEFT(@ErrorMessage, 1000);

    UPDATE dbo.CollectionsReceivableRunCompany
       SET Status = 'Failed', FinishedUtc = @now, RawRows = ISNULL(@RawRows, RawRows),
           ErrorNumber = @ErrorNumber, ErrorMessage = @msg, ShapeVariant = ISNULL(@ShapeVariant, ShapeVariant)
     WHERE RunId = @RunId AND CompanyId = @CompanyId;

    -- CurrentRunId, LastSuccessUtc and all coverage columns are deliberately NOT touched.
    UPDATE dbo.CollectionsReceivableCompanyState
       SET LastAttemptRunId = @RunId, LastAttemptUtc = @now, LastAttemptStatus = 'Failed',
           LastErrorNumber = @ErrorNumber, LastError = @msg, ConsecutiveFailures = ConsecutiveFailures + 1
     WHERE CompanyId = @CompanyId;
END;
GO

/*
  Closes runs that were started but never finished. A client timeout, a cancelled Hangfire job or an application restart aborts the whole
  T-SQL batch of dbo.usp_Collections_RefreshReceivables WITHOUT running its CATCH block, so the Run / RunCompany rows stay 'Running' and
  CompanyState keeps showing the previous failure. Idempotent and narrow:
    * @RunId given  -> only that run (and its company rows); NULL -> every run / company row still marked Running.
    * Only rows with Status = 'Running' are touched; terminal rows are never rewritten.
    * @RequireIdle = 1 (callers outside the refresh) first takes the refresh application lock, waiting up to @LockWaitSeconds. If a refresh still holds
      it, something IS running: nothing is changed and @Outcome = 'StillRunning'. The refresh procedure calls this with 0 because it holds the lock itself.
      Why a wait: a client timeout / cancellation does NOT stop a batch that is blocked inside the linked-server call - the session (and the lock) lives
      until the remote procedure returns (measured on SQL Server 2022), so the caller's first attempt usually finds the lock still held.
    * CompanyState is updated only when the closed attempt is newer than the state's last attempt, so an old orphan never overwrites a later result.
  The earlier published snapshot (CurrentRunId, coverage, LastSuccessUtc) is never touched.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_CloseReceivablesRun
    @RunId           uniqueidentifier = NULL,
    @ErrorNumber     int              = -1,
    @Message         nvarchar(1000)   = N'The refresh was interrupted before it finished (timeout, cancellation or application restart).',
    @RunStatus       varchar(20)      = 'Failed',      -- 'Failed' | 'Abandoned'
    @RequireIdle     bit              = 1,
    @LockWaitSeconds int              = 0,             -- 0 = do not wait; the application passes 120
    @ClosedRuns      int              = NULL OUTPUT,
    @ClosedCompanies int              = NULL OUTPUT,
    @Outcome         varchar(20)      = NULL OUTPUT    -- 'Closed' | 'StillRunning'
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @ClosedRuns = 0, @ClosedCompanies = 0, @Outcome = 'Closed';
    IF @RunStatus NOT IN ('Failed', 'Abandoned') THROW 50040, N'@RunStatus must be Failed or Abandoned.', 1;

    DECLARE @lock int, @now datetime2(3) = SYSUTCDATETIME(), @msg nvarchar(1000) = LEFT(@Message, 1000), @takenHere bit = 0, @lockMs int = 1000 * CASE WHEN @LockWaitSeconds < 0 THEN 0 WHEN @LockWaitSeconds > 600 THEN 600 ELSE @LockWaitSeconds END;
    IF @RequireIdle = 1
    BEGIN
        EXEC @lock = sys.sp_getapplock @Resource = N'Collections.ReceivablesRefresh', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @lockMs;
        IF @lock < 0 BEGIN SET @Outcome = 'StillRunning'; RETURN; END;
        SET @takenHere = 1;
    END;

    BEGIN TRY
        DECLARE @closed TABLE (RunId uniqueidentifier NOT NULL, CompanyId int NOT NULL, StartedUtc datetime2(3) NOT NULL);
        UPDATE dbo.CollectionsReceivableRunCompany
           SET Status = 'Failed', FinishedUtc = @now, ErrorNumber = @ErrorNumber, ErrorMessage = @msg
        OUTPUT inserted.RunId, inserted.CompanyId, inserted.StartedUtc INTO @closed
         WHERE Status = 'Running' AND (@RunId IS NULL OR RunId = @RunId);
        SET @ClosedCompanies = @@ROWCOUNT;

        -- The newest closed attempt per company becomes the company's last attempt, unless a later attempt already exists.
        UPDATE st
           SET LastAttemptRunId = c.RunId, LastAttemptUtc = @now, LastAttemptStatus = 'Failed',
               LastErrorNumber = @ErrorNumber, LastError = @msg, ConsecutiveFailures = st.ConsecutiveFailures + 1
          FROM dbo.CollectionsReceivableCompanyState st
          JOIN (SELECT x.CompanyId, x.RunId, x.StartedUtc, ROW_NUMBER() OVER (PARTITION BY x.CompanyId ORDER BY x.StartedUtc DESC) AS rn FROM @closed x) c
            ON c.CompanyId = st.CompanyId AND c.rn = 1
         WHERE st.LastAttemptUtc IS NULL OR st.LastAttemptUtc <= c.StartedUtc;

        UPDATE r
           SET Status = CASE WHEN EXISTS (SELECT 1 FROM dbo.CollectionsReceivableRunCompany k WHERE k.RunId = r.RunId AND k.Status = 'Succeeded') THEN 'PartialFailure' ELSE @RunStatus END,
               FinishedUtc = @now, Message = @msg
          FROM dbo.CollectionsReceivableRun r
         WHERE r.Status = 'Running' AND (@RunId IS NULL OR r.RunId = @RunId);
        SET @ClosedRuns = @@ROWCOUNT;
    END TRY
    BEGIN CATCH
        IF @takenHere = 1 EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';
        THROW;
    END CATCH;

    IF @takenHere = 1 EXEC sys.sp_releaseapplock @Resource = N'Collections.ReceivablesRefresh', @LockOwner = N'Session';
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_PublishReceivablesStaging
    @RunId                uniqueidentifier,
    @CompanyId            int,
    @CoverageFromDate     date,
    @CoverageThroughDate  date,
    @SourceMinAmount      int,
    @ShapeVariant         tinyint = NULL,
    @StatusColumnPresent  bit = 1,
    @MaxRawRows           int = 1000000,
    @MaxShrinkPercent     int = 60,
    @ShrinkGuardMinRows   int = 200,
    @AllowLargeShrink     bit = 0,
    @FetchMs              int = NULL,
    @RetainPaid           bit = 1,      -- keep fully paid (Amount = 0) instalments so "Fully paid" / "All" can be offered
    @BreakdownPresent     bit = 0       -- the staged rows carry PlanAmount / AllocatedAmount (companion procedure shape)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;

    DECLARE @now datetime2(3) = SYSUTCDATETIME(), @t0 datetime2(3) = SYSUTCDATETIME(), @tValidated datetime2(3), @validateMs int, @publishMs int;
    DECLARE @lo bigint, @hi bigint, @batch int = 1500, @unclassified int = 0, @exotic int = 0, @ownTran bit = 0, @ulo int, @uhi int;   -- 1500 rows x 3 indexes stays under the ~5000-lock escalation threshold
    DECLARE @raw int = 0, @nullKeys int, @wrongCompany int, @negative int, @zero int,
            @badUnit int, @badUnitAmt decimal(19,4), @badId int, @badIdAmt decimal(19,4),
            @contra int, @unknownStatus int, @published int, @previous int, @oldRun uniqueidentifier,
            @err nvarchar(1000);

    IF NOT EXISTS (SELECT 1 FROM dbo.CollectionsReceivableRunCompany WHERE RunId = @RunId AND CompanyId = @CompanyId)
        INSERT dbo.CollectionsReceivableRunCompany (RunId, CompanyId, StartedUtc, Status) VALUES (@RunId, @CompanyId, @now, 'Running');

    BEGIN TRY
        IF @CompanyId NOT IN (4, 32) THROW 50010, N'Unsupported company.', 1;

        SELECT @raw          = COUNT(*),
               @nullKeys     = ISNULL(SUM(CASE WHEN DueDate IS NULL OR Amount IS NULL THEN 1 ELSE 0 END), 0),
               @wrongCompany = ISNULL(SUM(CASE WHEN CompanyID IS NULL OR CompanyID <> @CompanyId THEN 1 ELSE 0 END), 0),
               @negative     = ISNULL(SUM(CASE WHEN Amount < 0 THEN 1 ELSE 0 END), 0),
               @zero         = ISNULL(SUM(CASE WHEN Amount = 0 THEN 1 ELSE 0 END), 0),
               @badUnit      = ISNULL(SUM(CASE WHEN Amount > 0 AND (UnitID IS NULL OR UnitID <= 0) THEN 1 ELSE 0 END), 0),
               @badUnitAmt   = ISNULL(SUM(CASE WHEN Amount > 0 AND (UnitID IS NULL OR UnitID <= 0) THEN Amount ELSE 0 END), 0),
               @badId        = ISNULL(SUM(CASE WHEN Amount > 0 AND UnitID > 0 AND NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NULL THEN 1 ELSE 0 END), 0),
               @badIdAmt     = ISNULL(SUM(CASE WHEN Amount > 0 AND UnitID > 0 AND NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NULL THEN Amount ELSE 0 END), 0),
               @contra       = ISNULL(SUM(CASE WHEN @StatusColumnPresent = 1 AND Amount > 0 AND UPPER(LTRIM(RTRIM(Status))) = N'PAID' THEN 1 ELSE 0 END), 0),
               @unknownStatus = ISNULL(SUM(CASE WHEN @StatusColumnPresent = 1 AND Status IS NOT NULL
                                                 AND UPPER(LTRIM(RTRIM(Status))) NOT IN (N'PAID', N'INSTALLMENT') THEN 1 ELSE 0 END), 0)
          FROM dbo.CollectionsReceivableStaging;

        SET @tValidated = SYSUTCDATETIME(); SET @validateMs = DATEDIFF(MILLISECOND, @t0, @tValidated);
        IF @raw = 0 THROW 50011, N'PACT returned no rows; an empty result is treated as a failed refresh, not as "no receivables".', 1;
        IF @raw > @MaxRawRows THROW 50012, N'PACT returned more rows than the configured maximum; nothing was published.', 1;
        IF @wrongCompany > 0 THROW 50013, N'PACT returned rows whose CompanyID is not the expected company; nothing was published.', 1;
        IF @nullKeys > 0 THROW 50014, N'PACT returned rows without a due date or amount; nothing was published.', 1;
        IF @negative > 0 THROW 50015, N'PACT returned negative remaining amounts; the amount meaning has changed, nothing was published.', 1;

        SELECT @published = COUNT(*)
          FROM dbo.CollectionsReceivableStaging
         WHERE (Amount > 0 OR (@RetainPaid = 1 AND Amount = 0)) AND UnitID > 0 AND NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NOT NULL;

        SELECT @previous = SnapshotRowCount, @oldRun = CurrentRunId FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @CompanyId;
        IF @oldRun IS NOT NULL AND @AllowLargeShrink = 0 AND @previous >= @ShrinkGuardMinRows
           AND @published * 100 < @previous * (100 - @MaxShrinkPercent)
        BEGIN
            SET @err = CONCAT(N'Published row count fell from ', @previous, N' to ', @published, N' (more than ', @MaxShrinkPercent,
                              N'%); the previous snapshot was kept. Re-run with @AllowLargeShrink = 1 after confirming the drop is real.');
            THROW 50016, @err, 1;
        END;

        -- Campaign preparation, step 1: one row per unit (tenant, unit id, unit code - the campaign's unit identity, binary collation) with its dense
        -- keys and static facts. Variants = the unit's rows do not all carry one byte-identical (name, phone, e-mail, project).
        CREATE TABLE #um
        (
            TenantT nvarchar(100) COLLATE Latin1_General_BIN2 NOT NULL, UId bigint NOT NULL, CodeT nvarchar(200) COLLATE Latin1_General_BIN2 NOT NULL,
            UnitSeq int NOT NULL, TenantSeq int NOT NULL, CodeSeq int NOT NULL, Variants bit NOT NULL,
            FullName nvarchar(400) NOT NULL, Mobile nvarchar(100) NOT NULL, Email nvarchar(400) NOT NULL, ProjectCode nvarchar(200) NOT NULL,
            TowerNumber nvarchar(20) NULL, PhoneId int NULL, EmailId int NULL, PhoneOk bit NULL, EmailOk bit NULL, PhoneVer tinyint NULL, EmailVer tinyint NULL,
            PRIMARY KEY (TenantT, UId, CodeT)
        );
        WITH a AS
        (
            SELECT LTRIM(RTRIM(TenantID)) COLLATE Latin1_General_BIN2 AS TenantT, UnitID AS UId, LTRIM(RTRIM(ISNULL(UnitCode, N''))) COLLATE Latin1_General_BIN2 AS CodeT,
                   MIN(ISNULL(FullName, N'') COLLATE Latin1_General_BIN2) AS Name1, MAX(ISNULL(FullName, N'') COLLATE Latin1_General_BIN2) AS Name2,
                   MIN(DATALENGTH(ISNULL(FullName, N''))) AS NameL1, MAX(DATALENGTH(ISNULL(FullName, N''))) AS NameL2,
                   MIN(ISNULL(Mobile, N'') COLLATE Latin1_General_BIN2) AS Mob1, MAX(ISNULL(Mobile, N'') COLLATE Latin1_General_BIN2) AS Mob2,
                   MIN(DATALENGTH(ISNULL(Mobile, N''))) AS MobL1, MAX(DATALENGTH(ISNULL(Mobile, N''))) AS MobL2,
                   MIN(ISNULL(Email, N'') COLLATE Latin1_General_BIN2) AS Eml1, MAX(ISNULL(Email, N'') COLLATE Latin1_General_BIN2) AS Eml2,
                   MIN(DATALENGTH(ISNULL(Email, N''))) AS EmlL1, MAX(DATALENGTH(ISNULL(Email, N''))) AS EmlL2,
                   MIN(ISNULL(ProjectCode, N'') COLLATE Latin1_General_BIN2) AS Prj1, MAX(ISNULL(ProjectCode, N'') COLLATE Latin1_General_BIN2) AS Prj2,
                   MIN(DATALENGTH(ISNULL(ProjectCode, N''))) AS PrjL1, MAX(DATALENGTH(ISNULL(ProjectCode, N''))) AS PrjL2
              FROM dbo.CollectionsReceivableStaging
             WHERE (Amount > 0 OR (@RetainPaid = 1 AND Amount = 0)) AND UnitID > 0 AND NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NOT NULL
             GROUP BY LTRIM(RTRIM(TenantID)) COLLATE Latin1_General_BIN2, UnitID, LTRIM(RTRIM(ISNULL(UnitCode, N''))) COLLATE Latin1_General_BIN2
        )
        INSERT #um (TenantT, UId, CodeT, UnitSeq, TenantSeq, CodeSeq, Variants, FullName, Mobile, Email, ProjectCode, TowerNumber)
        SELECT TenantT, UId, CodeT,
               ROW_NUMBER() OVER (ORDER BY TenantT, CodeT, UId), DENSE_RANK() OVER (ORDER BY TenantT), DENSE_RANK() OVER (ORDER BY TenantT, CodeT),
               CASE WHEN Name1 <> Name2 OR NameL1 <> NameL2 OR Mob1 <> Mob2 OR MobL1 <> MobL2 OR Eml1 <> Eml2 OR EmlL1 <> EmlL2
                      OR Prj1 <> Prj2 OR PrjL1 <> PrjL2 THEN 1 ELSE 0 END,
               Name1, Mob1, Eml1, Prj1, dbo.fn_CollectionsTowerNumber(CodeT)
          FROM a;

        -- Step 2: register every distinct phone / e-mail text (Norm stays NULL until the application normalises it). Small committed batches.
        SELECT IDENTITY(int, 1, 1) AS n, v.Kind, v.Raw INTO #newvals
          FROM (SELECT DISTINCT CAST(1 AS tinyint) AS Kind, Mobile COLLATE Latin1_General_BIN2 AS Raw FROM dbo.CollectionsReceivableStaging
                 WHERE ISNULL(Mobile, N'') <> N'' AND (Amount > 0 OR (@RetainPaid = 1 AND Amount = 0)) AND UnitID > 0
                UNION
                SELECT 2, Email COLLATE Latin1_General_BIN2 FROM dbo.CollectionsReceivableStaging
                 WHERE ISNULL(Email, N'') <> N'' AND (Amount > 0 OR (@RetainPaid = 1 AND Amount = 0)) AND UnitID > 0) v
         WHERE NOT EXISTS (SELECT 1 FROM dbo.CollectionsContactNorm c WHERE c.Kind = v.Kind AND c.Raw = v.Raw);
        SELECT @ulo = ISNULL(MIN(n), 1), @uhi = ISNULL(MAX(n), 0) FROM #newvals;
        WHILE @ulo <= @uhi
        BEGIN
            INSERT dbo.CollectionsContactNorm (Kind, Raw) SELECT Kind, Raw FROM #newvals WHERE n >= @ulo AND n < @ulo + @batch;
            SET @ulo += @batch;
        END;
        -- A unit's phone / e-mail validity is taken over from the dictionary when it already holds the value's normalisation (the common case: the same
        -- customers as the previous run); NULL = pending, filled by dbo.usp_Collections_SyncUnitContacts after the application normalised the new texts.
        UPDATE um SET PhoneId = p.ContactId, EmailId = e.ContactId,
                      PhoneOk = CASE WHEN um.Mobile = N'' THEN CAST(0 AS bit) WHEN p.Norm IS NULL THEN NULL WHEN p.Norm = N'' THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END,
                      EmailOk = CASE WHEN um.Email = N'' THEN CAST(0 AS bit) WHEN e.Norm IS NULL THEN NULL WHEN e.Norm = N'' THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END,
                      PhoneVer = CASE WHEN um.Mobile = N'' THEN NULL ELSE p.NormVersion END, EmailVer = CASE WHEN um.Email = N'' THEN NULL ELSE e.NormVersion END
          FROM #um um
          LEFT JOIN dbo.CollectionsContactNorm p ON p.Kind = 1 AND um.Mobile <> N'' AND p.Raw = um.Mobile COLLATE Latin1_General_BIN2
          LEFT JOIN dbo.CollectionsContactNorm e ON e.Kind = 2 AND um.Email <> N'' AND e.Raw = um.Email COLLATE Latin1_General_BIN2;

        -- Stage -> snapshot under the NEW run id, in small autocommit batches. Readers only ever read CurrentRunId, so they never touch
        -- these rows; the small batches additionally keep each statement below lock escalation, so a table-level lock can never
        -- stall a reader, and every batch commits (and releases its locks) on its own. Nothing is visible until the pointer flips.
        SELECT @lo = MIN(StagingRowId), @hi = MAX(StagingRowId) FROM dbo.CollectionsReceivableStaging;
        WHILE @lo <= @hi
        BEGIN
            INSERT dbo.CollectionsReceivableSnapshot
                (RunId, CompanyId, TenantId, FullName, Mobile, Email, UnitId, UnitCode, ProjectCode, TowerNumber,
                 VoucherNumber, ChequeNumber, DueDate, Amount, OriginalAmount, PaidAmount, PaymentStatus, SourceStatus, LoadedUtc,
                 UnitSeq, TenantSeq, RowFlags)
            SELECT @RunId, @CompanyId, LTRIM(RTRIM(TenantID)), ISNULL(FullName, N''), ISNULL(Mobile, N''), ISNULL(Email, N''),
                   UnitID, LTRIM(RTRIM(ISNULL(UnitCode, N''))), ISNULL(ProjectCode, N''), dbo.fn_CollectionsTowerNumber(UnitCode),
                   ISNULL(VoucherNumber, N''), ISNULL(ChequeNumber, N''), DueDate, Amount,
                   -- Original / paid amounts are stored ONLY when the source returned them AND they agree with the remaining amount
                   -- (Plan - Allocated = Remaining, within 0.01). They are never derived from anything else.
                   CASE WHEN c.Consistent = 1 THEN PlanAmount END, CASE WHEN c.Consistent = 1 THEN AllocatedAmount END,
                   CASE WHEN @BreakdownPresent = 1
                        THEN CASE WHEN c.Consistent = 0 THEN 'Unknown' WHEN Amount = 0 THEN 'FullyPaid' WHEN AllocatedAmount > 0 THEN 'PartiallyPaid' ELSE 'Unpaid' END
                        -- Deployed procedures: only "remaining = 0 and Status = Paid" is verifiable. Installment does NOT distinguish unpaid from partially paid.
                        ELSE CASE WHEN Amount = 0 AND UPPER(LTRIM(RTRIM(Status))) = N'PAID' THEN 'FullyPaid' ELSE 'Unknown' END END,
                   NULLIF(LTRIM(RTRIM(Status)), N''), @now,
                   um.UnitSeq, um.TenantSeq, CASE WHEN LTRIM(RTRIM(Status)) = N'Paid' THEN 1 ELSE 0 END
              FROM dbo.CollectionsReceivableStaging
              JOIN (SELECT TenantT, UId, CodeT, UnitSeq, TenantSeq FROM #um) um ON um.TenantT = LTRIM(RTRIM(TenantID)) COLLATE Latin1_General_BIN2 AND um.UId = UnitID
                         AND um.CodeT = LTRIM(RTRIM(ISNULL(UnitCode, N''))) COLLATE Latin1_General_BIN2
             CROSS APPLY (SELECT CASE WHEN @BreakdownPresent = 1 AND PlanAmount IS NOT NULL AND AllocatedAmount IS NOT NULL AND PlanAmount >= 0 AND AllocatedAmount >= 0
                                           AND ABS(PlanAmount - AllocatedAmount - Amount) <= 0.01 THEN 1 ELSE 0 END AS Consistent) c
             WHERE StagingRowId >= @lo AND StagingRowId < @lo + @batch
               AND (Amount > 0 OR (@RetainPaid = 1 AND Amount = 0)) AND UnitID > 0 AND NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NOT NULL;
            SET @lo += @batch;
        END;

        SELECT @ulo = ISNULL(MIN(UnitSeq), 1), @uhi = ISNULL(MAX(UnitSeq), 0) FROM #um;
        WHILE @ulo <= @uhi
        BEGIN
            INSERT dbo.CollectionsReceivableUnit (CompanyId, RunId, UnitSeq, TenantSeq, CodeSeq, UnitId, Variants, BadCode, PhoneId, EmailId, PhoneOk, EmailOk, PhoneVer, EmailVer)
            SELECT @CompanyId, @RunId, UnitSeq, TenantSeq, CodeSeq, UId, Variants,
                   CASE WHEN UId <= 0 OR LTRIM(RTRIM(CodeT)) = N'' OR CodeT = N'0' THEN 1 ELSE 0 END, PhoneId, EmailId, PhoneOk, EmailOk, PhoneVer, EmailVer
              FROM #um WHERE UnitSeq >= @ulo AND UnitSeq < @ulo + @batch ORDER BY UnitSeq;
            INSERT dbo.CollectionsReceivableUnitText (CompanyId, RunId, UnitSeq, TenantId, UnitCode, FullName, ProjectCode, TowerNumber)
            SELECT @CompanyId, @RunId, UnitSeq, TenantT, CodeT, FullName, ProjectCode, TowerNumber
              FROM #um WHERE UnitSeq >= @ulo AND UnitSeq < @ulo + @batch ORDER BY UnitSeq;
            SET @ulo += @batch;
        END;

        INSERT dbo.CollectionsReceivableTowerSummary (CompanyId, RunId, TowerNumber, RowCnt, Amount)
        SELECT @CompanyId, @RunId, TowerNumber, COUNT(*), SUM(Amount)
          FROM dbo.CollectionsReceivableSnapshot WHERE CompanyId = @CompanyId AND RunId = @RunId AND Amount > 0 GROUP BY TowerNumber;   -- outstanding receivables only

        -- Computed before the flip so the transaction below stays a handful of single-row updates.
        SELECT @unclassified = COUNT(*) FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @RunId AND CompanyId = @CompanyId AND PaymentStatus = 'Unknown';

        -- Text whose first / last character is whitespace that .NET trims but LTRIM/RTRIM does not (tab, NBSP, ...). The SQL campaign engine
        -- (V007) mirrors the application's trimming with LTRIM/RTRIM and is only used while this count is 0; otherwise the application evaluates
        -- the campaign itself. Computed before the flip, from the new run's outstanding rows only.
        DECLARE @cls nvarchar(100) = NCHAR(9) + N'-' + NCHAR(13) + NCHAR(133) + NCHAR(160) + NCHAR(5760) + NCHAR(8192) + N'-' + NCHAR(8202)
                                   + NCHAR(8232) + NCHAR(8233) + NCHAR(8239) + NCHAR(8287) + NCHAR(12288);
        DECLARE @lead nvarchar(120) = N'[' + @cls + N']%', @trail nvarchar(120) = N'%[' + @cls + N']';
        SELECT @exotic = COUNT(*) FROM dbo.CollectionsReceivableSnapshot s
         WHERE s.RunId = @RunId AND s.CompanyId = @CompanyId AND s.Amount > 0
           AND (   s.TenantId COLLATE Latin1_General_BIN2 LIKE @lead OR s.TenantId COLLATE Latin1_General_BIN2 LIKE @trail
                OR s.UnitCode COLLATE Latin1_General_BIN2 LIKE @lead OR s.UnitCode COLLATE Latin1_General_BIN2 LIKE @trail
                OR LTRIM(RTRIM(s.FullName)) COLLATE Latin1_General_BIN2 LIKE @lead OR LTRIM(RTRIM(s.FullName)) COLLATE Latin1_General_BIN2 LIKE @trail
                OR s.SourceStatus COLLATE Latin1_General_BIN2 LIKE @lead OR s.SourceStatus COLLATE Latin1_General_BIN2 LIKE @trail);

        BEGIN TRANSACTION; SET @ownTran = 1;
            UPDATE dbo.CollectionsReceivableCompanyState
               SET CurrentRunId = @RunId, LastAttemptRunId = @RunId, LastAttemptUtc = @now, LastAttemptStatus = 'Succeeded',
                   LastSuccessUtc = @now, LastErrorNumber = NULL, LastError = NULL, ConsecutiveFailures = 0,
                   SnapshotRowCount = @published, RawRowCount = @raw, ExcludedZeroRows = CASE WHEN @RetainPaid = 1 THEN 0 ELSE @zero END,
                   ExcludedInvalidUnitRows = @badUnit, ExcludedInvalidUnitAmount = @badUnitAmt,
                   ExcludedInvalidIdentityRows = @badId, ExcludedInvalidIdentityAmount = @badIdAmt,
                   ContradictoryStatusRows = @contra, UnknownStatusRows = @unknownStatus,
                   -- Paid instalments count as retained only if retention was requested, no minimum hid them, and the source really returned some.
                   PaidRetained = CASE WHEN @RetainPaid = 1 AND @SourceMinAmount = 0 AND @zero > 0 THEN 1 ELSE 0 END,
                   BreakdownAvailable = @BreakdownPresent,
                   UnclassifiedRows = @unclassified, ExoticTextRows = @exotic,
                   CoverageFromDate = @CoverageFromDate, CoverageThroughDate = @CoverageThroughDate,
                   SourceMinAmount = @SourceMinAmount, ShapeVariant = ISNULL(@ShapeVariant, ShapeVariant),
                   StatusColumnPresent = @StatusColumnPresent
             WHERE CompanyId = @CompanyId;

            UPDATE dbo.CollectionsReceivableRunCompany
               SET Status = 'Succeeded', FinishedUtc = @now, RawRows = @raw, PublishedRows = @published, FetchMs = @FetchMs, ValidateMs = @validateMs,
                   PublishMs = DATEDIFF(MILLISECOND, @tValidated, SYSUTCDATETIME()),
                   ExcludedZeroRows = CASE WHEN @RetainPaid = 1 THEN 0 ELSE @zero END, ExcludedInvalidUnitRows = @badUnit, ExcludedInvalidIdentityRows = @badId,
                   ShapeVariant = @ShapeVariant, ErrorNumber = NULL, ErrorMessage = NULL
             WHERE RunId = @RunId AND CompanyId = @CompanyId;
        COMMIT TRANSACTION; SET @ownTran = 0;

        -- Cleanup keeps the NEW run and the run it replaced (@oldRun) and removes everything older, in small batches. Keeping the replaced run for
        -- one more cycle is deliberate: a reader that started just before the flip may still be reading it, and deleting those rows under it
        -- would block (and, with the reader's own locks, deadlock) the two. Runs that old can no longer be read by anyone.
        WHILE 1 = 1
        BEGIN
            DELETE TOP (1500) FROM dbo.CollectionsReceivableSnapshot
             WHERE CompanyId = @CompanyId AND RunId <> @RunId AND RunId <> ISNULL(@oldRun, '00000000-0000-0000-0000-000000000000');
            IF @@ROWCOUNT = 0 BREAK;
        END;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (1500) FROM dbo.CollectionsReceivableUnit
             WHERE CompanyId = @CompanyId AND RunId <> @RunId AND RunId <> ISNULL(@oldRun, '00000000-0000-0000-0000-000000000000');
            IF @@ROWCOUNT = 0 BREAK;
        END;
        WHILE 1 = 1
        BEGIN
            DELETE TOP (1500) FROM dbo.CollectionsReceivableUnitText
             WHERE CompanyId = @CompanyId AND RunId <> @RunId AND RunId <> ISNULL(@oldRun, '00000000-0000-0000-0000-000000000000');
            IF @@ROWCOUNT = 0 BREAK;
        END;
        DELETE FROM dbo.CollectionsReceivableTowerSummary
         WHERE CompanyId = @CompanyId AND RunId <> @RunId AND RunId <> ISNULL(@oldRun, '00000000-0000-0000-0000-000000000000');
        DELETE FROM dbo.CollectionsReceivableStaging;
    END TRY
    BEGIN CATCH
        IF @ownTran = 1 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;   -- only the flip transaction this procedure started; a caller's transaction is never rolled back here
        DECLARE @n int = ERROR_NUMBER(), @m nvarchar(4000) = ERROR_MESSAGE();
        -- Partially inserted rows of this run were never published; remove them.
        DELETE FROM dbo.CollectionsReceivableSnapshot
         WHERE RunId = @RunId AND CompanyId = @CompanyId
           AND RunId <> ISNULL((SELECT CurrentRunId FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @CompanyId), '00000000-0000-0000-0000-000000000000');
        DELETE FROM dbo.CollectionsReceivableTowerSummary WHERE RunId = @RunId AND CompanyId = @CompanyId
           AND RunId <> ISNULL((SELECT CurrentRunId FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @CompanyId), '00000000-0000-0000-0000-000000000000');
        DELETE FROM dbo.CollectionsReceivableUnit WHERE RunId = @RunId AND CompanyId = @CompanyId
           AND RunId <> ISNULL((SELECT CurrentRunId FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @CompanyId), '00000000-0000-0000-0000-000000000000');
        DELETE FROM dbo.CollectionsReceivableUnitText WHERE RunId = @RunId AND CompanyId = @CompanyId
           AND RunId <> ISNULL((SELECT CurrentRunId FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = @CompanyId), '00000000-0000-0000-0000-000000000000');
        EXEC dbo.usp_Collections_RecordReceivablesFailure @RunId, @CompanyId, @n, @m, @raw, @ShapeVariant;
        DELETE FROM dbo.CollectionsReceivableStaging;
    END CATCH;
END;
GO
