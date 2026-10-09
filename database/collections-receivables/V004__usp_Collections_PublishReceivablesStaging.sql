/*
  V004 - Validate and publish the staged rows of ONE company, or record a failure and keep the previous snapshot.

  dbo.usp_Collections_RecordReceivablesFailure   records a failed attempt for a company (previous snapshot untouched)
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
    DECLARE @lo bigint, @hi bigint, @batch int = 1500, @unclassified int = 0, @ownTran bit = 0;   -- 1500 rows x 3 indexes stays under the ~5000-lock escalation threshold
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

        -- Stage -> snapshot under the NEW run id, in small autocommit batches. Readers only ever read CurrentRunId, so they never touch
        -- these rows; the small batches additionally keep each statement below lock escalation, so a table-level lock can never
        -- stall a reader, and every batch commits (and releases its locks) on its own. Nothing is visible until the pointer flips.
        SELECT @lo = MIN(StagingRowId), @hi = MAX(StagingRowId) FROM dbo.CollectionsReceivableStaging;
        WHILE @lo <= @hi
        BEGIN
            INSERT dbo.CollectionsReceivableSnapshot
                (RunId, CompanyId, TenantId, FullName, Mobile, Email, UnitId, UnitCode, ProjectCode, TowerNumber,
                 VoucherNumber, ChequeNumber, DueDate, Amount, OriginalAmount, PaidAmount, PaymentStatus, SourceStatus, LoadedUtc)
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
                   NULLIF(LTRIM(RTRIM(Status)), N''), @now
              FROM dbo.CollectionsReceivableStaging
             CROSS APPLY (SELECT CASE WHEN @BreakdownPresent = 1 AND PlanAmount IS NOT NULL AND AllocatedAmount IS NOT NULL AND PlanAmount >= 0 AND AllocatedAmount >= 0
                                           AND ABS(PlanAmount - AllocatedAmount - Amount) <= 0.01 THEN 1 ELSE 0 END AS Consistent) c
             WHERE StagingRowId >= @lo AND StagingRowId < @lo + @batch
               AND (Amount > 0 OR (@RetainPaid = 1 AND Amount = 0)) AND UnitID > 0 AND NULLIF(LTRIM(RTRIM(TenantID)), N'') IS NOT NULL;
            SET @lo += @batch;
        END;

        INSERT dbo.CollectionsReceivableTowerSummary (CompanyId, RunId, TowerNumber, RowCnt, Amount)
        SELECT @CompanyId, @RunId, TowerNumber, COUNT(*), SUM(Amount)
          FROM dbo.CollectionsReceivableSnapshot WHERE CompanyId = @CompanyId AND RunId = @RunId AND Amount > 0 GROUP BY TowerNumber;   -- outstanding receivables only

        -- Computed before the flip so the transaction below stays a handful of single-row updates.
        SELECT @unclassified = COUNT(*) FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @RunId AND CompanyId = @CompanyId AND PaymentStatus = 'Unknown';

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
                   UnclassifiedRows = @unclassified,
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
        EXEC dbo.usp_Collections_RecordReceivablesFailure @RunId, @CompanyId, @n, @m, @raw, @ShapeVariant;
        DELETE FROM dbo.CollectionsReceivableStaging;
    END CATCH;
END;
GO
