/*
  SMOKE TEST of dbo.p4AccountReceivablesV2 against the ORIGINAL dbo.p4AccountReceivables. READ-ONLY (temp tables only).
  Run after 10-/11- and BEFORE switching the application. Expectations are stated per check; investigate any non-zero "expect 0".
  The original returns each instalment once per ledger account x invoice (see Receivables-Source-Review.md D1), so its row count is
  expected to be HIGHER; compare totals per Tag only after reading that note.
*/
SET NOCOUNT ON;
DECLARE @s datetime = '2026-01-01', @e datetime = '2026-12-31T23:59:59.997';

IF OBJECT_ID('tempdb..#v2') IS NOT NULL DROP TABLE #v2;
CREATE TABLE #v2 (CompanyID int, ProjectCode nvarchar(200) NULL, UnitCode nvarchar(200) NULL, TenantID nvarchar(50), FullName nvarchar(500) NULL,
    Mobile nvarchar(200) NULL, Email nvarchar(500) NULL, UnitID bigint NULL, VoucherNumber nvarchar(200) NULL, ChequeNumber nvarchar(200) NULL,
    DueDate datetime, Amount decimal(19,4), Status varchar(20), PaymentTermAccountId bigint, PlanAmount decimal(19,4), AllocatedAmount decimal(19,4));
-- @IncludeSettled = 1 so settled instalments (Remaining = 0) are visible for the arithmetic checks.
INSERT #v2 EXEC dbo.p4AccountReceivablesV2 @StartDate = @s, @EndDate = @e, @MinAmount = 0, @IncludeSettled = 1, @StrictIdentity = 1;

SELECT N'V2 rows (incl. settled)' AS Metric, COUNT(*) AS Value FROM #v2
UNION ALL SELECT N'V2 open rows (Amount > 0)', COUNT(*) FROM #v2 WHERE Amount > 0
UNION ALL SELECT N'V2 distinct tenants with an open balance', COUNT(DISTINCT TenantID) FROM #v2 WHERE Amount > 0
UNION ALL SELECT N'expect 0: duplicate instalment identity (Tenant, Voucher, Account, DueDate)', COUNT(*) FROM
    (SELECT 1 x FROM #v2 GROUP BY TenantID, VoucherNumber, PaymentTermAccountId, DueDate HAVING COUNT(*) > 1) d
UNION ALL SELECT N'expect 0: original <> paid + remaining (PlanAmount <> AllocatedAmount + Amount)', COUNT(*) FROM #v2 WHERE ABS(PlanAmount - AllocatedAmount - Amount) >= 0.00005
UNION ALL SELECT N'expect 0: negative PlanAmount, AllocatedAmount or Amount', COUNT(*) FROM #v2 WHERE PlanAmount < 0 OR AllocatedAmount < 0 OR Amount < 0
UNION ALL SELECT N'expect 0: AllocatedAmount above PlanAmount', COUNT(*) FROM #v2 WHERE AllocatedAmount > PlanAmount
UNION ALL SELECT N'expect 0: payment applied out of FIFO order (a later instalment paid while an earlier one is not fully paid)', COUNT(*) FROM #v2 a
    JOIN #v2 b ON b.TenantID = a.TenantID AND (b.DueDate > a.DueDate OR (b.DueDate = a.DueDate AND (b.VoucherNumber > a.VoucherNumber OR (b.VoucherNumber = a.VoucherNumber AND b.PaymentTermAccountId > a.PaymentTermAccountId))))
    WHERE a.AllocatedAmount < a.PlanAmount AND b.AllocatedAmount > 0
UNION ALL SELECT N'review: open rows with more than two decimals (genuine sub-fils; stays "AmountPrecisionNeedsReview")', COUNT(*) FROM #v2 WHERE Amount > 0 AND Amount <> ROUND(Amount, 2)
UNION ALL SELECT N'review: open rows below AED 1', COUNT(*) FROM #v2 WHERE Amount > 0 AND Amount < 1
UNION ALL SELECT N'review: rows with no unit (UnitID is null)', COUNT(*) FROM #v2 WHERE Amount > 0 AND UnitID IS NULL
UNION ALL SELECT N'review: rows with no mobile', COUNT(*) FROM #v2 WHERE Amount > 0 AND (Mobile IS NULL OR LTRIM(RTRIM(Mobile)) = N'')
UNION ALL SELECT N'review: partly paid open rows (0 < AllocatedAmount < PlanAmount)', COUNT(*) FROM #v2 WHERE Amount > 0 AND AllocatedAmount > 0
UNION ALL SELECT N'review: unpaid open rows (AllocatedAmount = 0)', COUNT(*) FROM #v2 WHERE Amount > 0 AND AllocatedAmount = 0;

-- The original, for the fan-out comparison (the same window; @MinAmount = 0 as the application calls it).
IF OBJECT_ID('tempdb..#orig') IS NOT NULL DROP TABLE #orig;
CREATE TABLE #orig (CompanyID int, ProjectCode nvarchar(200) NULL, UnitCode nvarchar(200) NULL, TenantID nvarchar(50), FullName nvarchar(500) NULL, Mobile nvarchar(200) NULL, Email nvarchar(500) NULL, UnitID bigint NULL, VoucherNumber nvarchar(200) NULL, ChequeNumber nvarchar(200) NULL, DueDate datetime, Amount float, Status varchar(20));
INSERT #orig EXEC dbo.p4AccountReceivables @StartDate = @s, @EndDate = @e, @MinAmount = 0;
SELECT N'original rows (Amount > 0)' AS Metric, COUNT(*) AS Value FROM #orig WHERE Amount > 0
UNION ALL SELECT N'original distinct (Tenant, DueDate, Amount) with Amount > 0', COUNT(*) FROM (SELECT DISTINCT TenantID, DueDate, Amount FROM #orig WHERE Amount > 0) x;
-- Per tenant: original vs V2 open balance. Differences are expected where the original fanned out or double counted;
-- every difference must be explained (or reported to the source owner) before FinancialSourceValidated is set. See ../reconciliation-checklist.md
SELECT TOP 200 v.TenantID, v.V2Open, o.OriginalDistinctOpen, v.V2Open - o.OriginalDistinctOpen AS Difference
FROM (SELECT TenantID, SUM(Amount) V2Open FROM #v2 WHERE Amount > 0 GROUP BY TenantID) v
FULL JOIN (SELECT TenantID, SUM(Amount) OriginalDistinctOpen FROM (SELECT DISTINCT TenantID, DueDate, Amount FROM #orig WHERE Amount > 0) d GROUP BY TenantID) o ON o.TenantID = v.TenantID
WHERE ABS(COALESCE(v.V2Open, 0) - COALESCE(o.OriginalDistinctOpen, 0)) >= 0.01
ORDER BY ABS(COALESCE(v.V2Open, 0) - COALESCE(o.OriginalDistinctOpen, 0)) DESC;
