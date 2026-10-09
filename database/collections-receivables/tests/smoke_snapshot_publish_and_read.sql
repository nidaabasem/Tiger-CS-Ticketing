/*
  SQL smoke test for publish validation and the read procedure. Needs NO linked server: it fills the staging table with
  synthetic rows and calls dbo.usp_Collections_PublishReceivablesStaging / dbo.usp_Collections_GetReceivables directly.
  Everything runs inside a transaction that is ROLLED BACK at the end (no data kept), but run it on a development copy:
  it temporarily replaces dbo.CollectionsReceivableCompanyState/Snapshot contents inside that transaction.
  Any failed expectation raises error 51000+ and the transaction is rolled back. Not executed in the repository CI (no SQL Server).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @run uniqueidentifier = NEWID();
INSERT dbo.CollectionsReceivableRun (RunId, TriggerSource, StartedUtc, Status) VALUES (@run, N'smoke', SYSUTCDATETIME(), 'Running');

-- Fake towers on a throw-away company id are not possible (CHECK 4/32), so use real ids but unmistakable tower numbers.
INSERT dbo.CollectionsTowers (TowerNumber, TowerName, CompanyId, IsActive) VALUES (N'9001', N'Smoke Tower A', 4, 1), (N'9003', N'Smoke Inactive', 4, 0);

DELETE FROM dbo.CollectionsReceivableStaging;
INSERT dbo.CollectionsReceivableStaging (CompanyID, ProjectCode, UnitCode, TenantID, FullName, Mobile, Email, UnitID, VoucherNumber, ChequeNumber, DueDate, Amount, Status)
VALUES
 (4, N'', N'TP9001-101',  N'T1', N'A',  N'1', N'a@x', 1, N'V1', N'', '20260930', 100.0000, N'Installment'),  -- overdue (before Oct)
 (4, N'', N'9001-102',    N'T2', N'B',  N'1', N'b@x', 2, N'V2', N'', '20261001', 50.0000,  N'Installment'),  -- due: first day of month
 (4, N'', N'TP9001-C-103',N'T3', N'C',  N'1', N'c@x', 3, N'V3', N'', '20261031', 25.0000,  N'Installment'),  -- due: last day of month
 (4, N'', N'TP9001-104',  N'T4', N'D',  N'1', N'd@x', 4, N'V4', N'', '20261101', 10.0000,  N'Installment'),  -- Outstanding: after the month, neither Due nor Overdue
 (4, N'', N'TP9001-105',  N'T5', N'E',  N'1', N'e@x', 5, N'V5', N'', '20261015', 0.0000,   N'Paid'),         -- paid: excluded
 (4, N'', N'TP9001-106',  N'T6', N'F',  N'1', N'f@x', 0, N'V6', N'', '20261015', 70.0000,  N'Installment'),  -- UnitID 0: excluded + counted
 (4, N'', N'TP119-107',   N'T7', N'G',  N'1', N'g@x', 7, N'V7', N'', '20261015', 5.5000,   N'Installment'),  -- unmatched tower 119
 (4, N'', N'TP9003-108',  N'T8', N'H',  N'1', N'h@x', 8, N'V8', N'', '20261015', 6.0000,   N'Installment'),  -- inactive tower
 (4, N'', N'TP9001-109',  N'T9', N'I',  N'1', N'i@x', 9, N'V9', N'', '20261031 23:00', 7.0000, N'Installment'); -- To = 31 Oct must include the whole day

EXEC dbo.usp_Collections_PublishReceivablesStaging @RunId = @run, @CompanyId = 4, @CoverageFromDate = '20000101', @CoverageThroughDate = '20991231',
     @SourceMinAmount = 0, @ShapeVariant = 1, @StatusColumnPresent = 1, @AllowLargeShrink = 1, @RetainPaid = 1;

IF (SELECT LastAttemptStatus FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 'Succeeded' THROW 51001, N'publish should have succeeded', 1;
IF (SELECT SnapshotRowCount FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 8 THROW 51002, N'expected 8 published rows (9 staged - 1 UnitID 0; the paid row is RETAINED)', 1;
IF (SELECT PaidRetained FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 1 THROW 51011, N'paid instalments were retained, so PaidRetained must be 1', 1;
IF (SELECT BreakdownAvailable FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 0 THROW 51012, N'the deployed shape returns no original/paid amounts', 1;
IF (SELECT PaymentStatus FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @run AND TenantId = N'T5') <> 'FullyPaid' THROW 51013, N'remaining 0 + Status Paid is the one verifiable status', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @run AND Amount > 0 AND PaymentStatus <> 'Unknown') THROW 51014, N'a positive remainder from the deployed shape must stay Unknown (Installment does not distinguish unpaid from partially paid)', 1;
IF EXISTS (SELECT 1 FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @run AND (OriginalAmount IS NOT NULL OR PaidAmount IS NOT NULL)) THROW 51015, N'original/paid amounts must never be invented', 1;
IF (SELECT ExcludedInvalidUnitRows FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 1 THROW 51003, N'UnitID 0 row must be counted', 1;
IF (SELECT TowerNumber FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @run AND TenantId = N'T3') <> N'9001' THROW 51004, N'TP9001-C-103 must map to tower 9001', 1;

-- Read: Tower A, whole of Oct 2026, as of 7 Oct => T2 (Due), T3 (Due), T9 (Due, 23:00 on the To day), T1 is before From.
DECLARE @tower int = (SELECT TowerId FROM dbo.CollectionsTowers WHERE CompanyId = 4 AND TowerNumber = N'9001');
-- Result sets cannot be captured by INSERT-EXEC when there are several, so assert through the same predicate the procedure uses:
DECLARE @cnt int = (SELECT COUNT(*) FROM dbo.CollectionsReceivableSnapshot s
    WHERE s.RunId = @run AND s.Amount > 0 AND s.TowerNumber = N'9001' AND s.DueDate >= '20261001' AND s.DueDate < DATEADD(DAY, 1, CAST('20261031' AS datetime)) AND s.DueDate < '20261101');
IF @cnt <> 3 THROW 51005, N'window/tower predicate should select T2, T3, T9', 1;
EXEC dbo.usp_Collections_GetReceivables @TowerId = @tower, @FromDate = '20260101', @ToDate = '20261031', @AsOfDate = '20261007';   -- eyeball: 4 rows (T1 overdue; T2,T3,T9 due), no T4/T5/T6
-- Instalment list: payment filter outstanding excludes the paid row; paid/all need PaidRetained; unpaid/partial are Unavailable for the deployed shape; min is ignored for paid/all.
EXEC dbo.usp_Collections_GetInstalmentsPage @TowerId = @tower, @FromDate = '20260101', @ToDate = '20261231', @AsOfDate = '20261007', @MinAmount = 100, @PaymentFilter = 'outstanding';
EXEC dbo.usp_Collections_GetInstalmentsPage @TowerId = @tower, @FromDate = '20260101', @ToDate = '20261231', @AsOfDate = '20261007', @MinAmount = 100000, @PaymentFilter = 'paid';   -- T5 still listed
EXEC dbo.usp_Collections_GetInstalmentsPage @TowerId = @tower, @FromDate = '20260101', @ToDate = '20261231', @AsOfDate = '20261007', @MinAmount = 0, @PaymentFilter = 'partial';  -- Unavailable = 1
EXEC dbo.usp_Collections_GetReceivables @TowerId = NULL,   @FromDate = '20260101', @ToDate = '20261231', @AsOfDate = '20261007', @ReceivableClass = 'Any';  -- includes T4 (Outstanding); result set 4 lists 119 and 9003

-- Failure keeps the previous snapshot: stage an empty result for company 4.
DECLARE @run2 uniqueidentifier = NEWID();
INSERT dbo.CollectionsReceivableRun (RunId, TriggerSource, StartedUtc, Status) VALUES (@run2, N'smoke', SYSUTCDATETIME(), 'Running');
DELETE FROM dbo.CollectionsReceivableStaging;
EXEC dbo.usp_Collections_PublishReceivablesStaging @RunId = @run2, @CompanyId = 4, @CoverageFromDate = '20000101', @CoverageThroughDate = '20991231', @SourceMinAmount = 0;
IF (SELECT CurrentRunId FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> @run THROW 51006, N'an empty result must not replace the snapshot', 1;
IF (SELECT ConsecutiveFailures FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 1 THROW 51007, N'failure must be counted', 1;
IF (SELECT LastAttemptStatus FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 4) <> 'Failed' THROW 51008, N'failure must be recorded', 1;
IF (SELECT COUNT(*) FROM dbo.CollectionsReceivableSnapshot WHERE RunId = @run) <> 8 THROW 51009, N'previous rows must survive a failed refresh', 1;

-- Company 32 is independent: the failed company-4 attempt must not touch its state.
IF EXISTS (SELECT 1 FROM dbo.CollectionsReceivableCompanyState WHERE CompanyId = 32 AND LastAttemptRunId IN (@run, @run2)) THROW 51010, N'company 32 must be untouched', 1;
PRINT N'Smoke test passed (rolling back).';
ROLLBACK TRANSACTION;
