/*
  READ-ONLY RECONCILIATION - company 32 (PACT2C32). Safe to run on production: every statement against PACT tables is a SELECT
  (WITH NOLOCK); results go to tempdb temporary tables only; no PACT object is created, altered or written.
  The ledger block (R1b) is as heavy as the procedure itself - run off-peak. For the other company, use the generated company file.

  Sections: R0 load | R1 fan-out of the original output | R1b invoices x accounts and the repeated PaidAmount | R2 invoice lines
  per instalment voucher | R3 identity ambiguity | R4 same-date ties | R5 voucher-number collisions | R6 ledger double-join
  | R7 plan vs invoice totals | R8 plans/ledger mismatches, negative plans | R9 original vs V2 per tag | R10 zero rows, float
  residue, MinAmount=0 effect | R11 unit mapping.
*/
SET NOCOUNT ON;
DECLARE @Year int = YEAR(GETDATE()), @Month int = MONTH(GETDATE());     -- reporting month to test
DECLARE @MonthStart datetime = DATEFROMPARTS(@Year, @Month, 1);
DECLARE @MonthEnd datetime = DATEADD(MILLISECOND, -3, DATEADD(MONTH, 1, @MonthStart));   -- last SQL datetime of the month (23:59:59.997)
DECLARE @From FLOAT = CONVERT(FLOAT, CONVERT(DATETIME, '01-01-2000')), @To FLOAT = CONVERT(FLOAT, CONVERT(DATETIME, '01-01-2099'));

IF OBJECT_ID('tempdb..#orig') IS NOT NULL DROP TABLE #orig;
IF OBJECT_ID('tempdb..#v2') IS NOT NULL DROP TABLE #v2;
IF OBJECT_ID('tempdb..#acct') IS NOT NULL DROP TABLE #acct;
IF OBJECT_ID('tempdb..#invu') IS NOT NULL DROP TABLE #invu;
IF OBJECT_ID('tempdb..#bal') IS NOT NULL DROP TABLE #bal;
IF OBJECT_ID('tempdb..#plan') IS NOT NULL DROP TABLE #plan;

-- R0: original output (MinAmount = 0, as the application calls it) and, if deployed, the V2 output.
CREATE TABLE #orig (CompanyID int,UnitCode varchar(120),TenantID varchar(120),FullName varchar(1200),Mobile varchar(200),Email varchar(200),UnitID bigint,VoucherNumber varchar(120),ChequeNumber varchar(50),DueDate datetime,Amount float,Status varchar(20));
DECLARE @t0 datetime2 = SYSDATETIME();
INSERT INTO #orig EXEC dbo.p32AccountReceivables @StartDate = '2000-01-01', @EndDate = @MonthEnd, @MinAmount = 0;
SELECT 'R0 original procedure' AS Section, COUNT(*) AS Rows_, SUM(CASE WHEN Amount = 0 THEN 1 ELSE 0 END) AS ZeroAmountRows,
       DATEDIFF(MILLISECOND, @t0, SYSDATETIME()) AS ElapsedMs FROM #orig;

SELECT DISTINCT T.AccountID INTO #acct
FROM PACT2C32..ACC_Accounts T WITH(NOLOCK), PACT2C32..ACC_Accounts GT WITH(NOLOCK)
WHERE T.lft BETWEEN GT.lft AND GT.rgt AND GT.AccountID IN (4010);

SELECT CONVERT(varchar(20), dcc.dcCCNID7) Tag, inv.VoucherNo, SUM(DCN.dcNum6) InvAmount, f.Name FlatUnit, DCT.dcAlpha5 PayMode, COUNT(*) LineRows
INTO #invu
FROM PACT2C32..Inv_DocDetails Inv WITH(NOLOCK)
INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_DocTextData DCT WITH(NOLOCK) ON DCT.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_DocNumData DCN WITH(NOLOCK) ON DCN.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_CC50010 f WITH(NOLOCK) ON dcc.dcCCNID10=f.NodeID
WHERE inv.CostCenterID=41013 AND inv.StatusId=369
GROUP BY dcc.dcCCNID7, inv.VoucherNo, f.Name, DCT.dcAlpha5;

-- R1: how many times is each instalment returned? (original joins #tab = accounts x invoices to every instalment of the tag)
SELECT 'R1 fan-out' AS Section, COUNT(*) AS OutputRows, COUNT(DISTINCT CONCAT(TenantID, '|', CONVERT(varchar(30), DueDate, 121))) AS DistinctTagDueDate,
       CAST(COUNT(*) AS float) / NULLIF(COUNT(DISTINCT CONCAT(TenantID, '|', CONVERT(varchar(30), DueDate, 121))), 0) AS AvgCopies
FROM #orig;
SELECT TOP (200) 'R1 detail: tag/date returned more than once' AS Section, TenantID, DueDate, COUNT(*) AS Copies,
       COUNT(DISTINCT VoucherNumber) AS DistinctVouchers, COUNT(DISTINCT UnitCode) AS DistinctUnits, MIN(Amount) AS MinAmt, MAX(Amount) AS MaxAmt
FROM #orig GROUP BY TenantID, DueDate HAVING COUNT(*) > 1 ORDER BY COUNT(*) DESC;

-- R1b: ledger balance per (account, tag) - the original ledger block, unchanged - then invoices x accounts per tag and the repeated PaidAmount.
SELECT A1.AccountID, ACC.TagID Tag, ((OP_Dr + TR_Dr) - (OP_Cr + TR_Cr)) Balance
INTO #bal
FROM (
SELECT AccountID,TagID,SUM(OP_Dr) OP_Dr,SUM(OP_Cr) OP_Cr,SUM(TR_Dr) TR_Dr,SUM(TR_Cr) TR_Cr
FROM(
SELECT AccountID,VoucherNo,TagID,
CASE WHEN SUM(OP_Dr)-SUM(OP_Cr)>0 THEN SUM(OP_Dr)-SUM(OP_Cr) ELSE 0 END OP_Dr,
CASE WHEN SUM(OP_Dr)-SUM(OP_Cr)<0 THEN SUM(OP_Cr)-SUM(OP_Dr) ELSE 0 END OP_Cr,
CASE WHEN SUM(TR_Dr)-SUM(TR_Cr)>0 THEN SUM(TR_Dr)-SUM(TR_Cr) ELSE 0 END TR_Dr,
CASE WHEN SUM(TR_Dr)-SUM(TR_Cr)<0 THEN SUM(TR_Cr)-SUM(TR_Dr) ELSE 0 END TR_Cr
FROM (
SELECT DebitAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,ACC.Amount OP_Dr,0 OP_Cr,0 TR_Dr,0 TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.AccDocDetailsID=ACC.AccDocDetailsID
WHERE ((ACC.DocumentType=16 AND (ACC.StatusID=369 OR ACC.StatusID=429)) OR (DocDate<@From AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))))
UNION ALL
SELECT CreditAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,0 OP_Dr,ACC.Amount OP_Cr,0 TR_Dr,0 TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.AccDocDetailsID=ACC.AccDocDetailsID
WHERE ((ACC.DocumentType=16 AND (ACC.StatusID=369 OR ACC.StatusID=429)) OR (DocDate<@From AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))))
UNION ALL
SELECT DebitAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,0 OP_Dr,0 OP_Cr,Amount TR_Dr,0 TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.AccDocDetailsID=ACC.AccDocDetailsID
WHERE (DocDate BETWEEN @From AND @To) AND ACC.DocumentType<>16 AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))
UNION ALL
SELECT CreditAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,0 OP_Dr,0 OP_Cr,0 TR_Dr,Amount TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.AccDocDetailsID=ACC.AccDocDetailsID
WHERE (DocDate BETWEEN @From AND @To) AND ACC.DocumentType<>16 AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))
UNION ALL
SELECT DebitAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,ACC.Amount OP_Dr,0 OP_Cr,0 TR_Dr,0 TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=ACC.InvDocDetailsID
WHERE ((ACC.DocumentType=16 AND (ACC.StatusID=369 OR ACC.StatusID=429)) OR (DocDate<@From AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))))
UNION ALL
SELECT CreditAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,0 OP_Dr,ACC.Amount OP_Cr,0 TR_Dr,0 TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=ACC.InvDocDetailsID
WHERE ((ACC.DocumentType=16 AND (ACC.StatusID=369 OR ACC.StatusID=429)) OR (DocDate<@From AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))))
UNION ALL
SELECT DebitAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,0 OP_Dr,0 OP_Cr,Amount TR_Dr,0 TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=ACC.InvDocDetailsID
WHERE (DocDate BETWEEN @From AND @To) AND ACC.DocumentType<>16 AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))
UNION ALL
SELECT CreditAccount AccountID,VoucherNo,DCC.dcCCNID7 TagID,0 OP_Dr,0 OP_Cr,0 TR_Dr,Amount TR_Cr
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK) INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=ACC.InvDocDetailsID
WHERE (DocDate BETWEEN @From AND @To) AND ACC.DocumentType<>16 AND (ACC.DocumentType<>14 AND ACC.DocumentType<>19 AND (ACC.StatusID=369 OR ACC.StatusID=429))
) AS T1 GROUP BY AccountID,VoucherNo,TagID
) AS T2 GROUP BY AccountID,TagID
) AS ACC INNER JOIN PACT2C32..ACC_Accounts A1 WITH(NOLOCK) ON A1.AccountID=ACC.AccountID
WHERE A1.AccountID>1 AND A1.AccountID IN (SELECT AccountID FROM #acct) AND ACC.TagID IS NOT NULL;

;WITH I AS (SELECT Tag, COUNT(*) InvoiceRows, SUM(CONVERT(float, InvAmount)) InvTotal FROM #invu GROUP BY Tag),
      A AS (SELECT CONVERT(varchar(20), Tag) Tag, COUNT(*) AccountRows, SUM(Balance) BalTotal FROM #bal GROUP BY CONVERT(varchar(20), Tag))
SELECT TOP (500) 'R1b tags where #tab fans out (accounts x invoices > 1)' AS Section, A.Tag, A.AccountRows, I.InvoiceRows,
       A.AccountRows * I.InvoiceRows AS TabRows,
       (A.AccountRows * I.InvoiceRows) * 1.0 AS InstalmentCopies,
       I.InvTotal - A.BalTotal AS CorrectPaid,                                   -- each invoice and balance once
       A.AccountRows * I.InvTotal - I.InvoiceRows * A.BalTotal AS OriginalSumPaid -- SUM(#tab.PaidAmount): InvAmount - Balance over accounts x invoices
FROM A JOIN I ON I.Tag = A.Tag WHERE A.AccountRows * I.InvoiceRows > 1 ORDER BY A.AccountRows * I.InvoiceRows DESC;

-- Instalments as the original builds @tabpay (but before collapsing), to measure line multiplication.
SELECT CONVERT(varchar(20), dcc.dcCCNID7) Tag, pt.VoucherNo, pt.AccountID AccountId, CONVERT(datetime, pt.DueDate) DueDate, SUM(pt.Amount) PlanAmount_Original, COUNT(*) JoinedRows
INTO #plan
FROM PACT2C32..Inv_DocDetails Inv WITH(NOLOCK)
INNER JOIN PACT2C32..COM_DocCCData DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_DocTextData DCT WITH(NOLOCK) ON DCT.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_DocPayTerms pt WITH(NOLOCK) ON inv.VoucherNo=pt.VoucherNo
WHERE inv.CostCenterID=41013 AND inv.StatusId=369 AND UPPER(DCT.dcAlpha5)=UPPER('Installment')
  AND pt.AccountID IN (SELECT AccountID FROM #acct)
GROUP BY dcc.dcCCNID7, pt.VoucherNo, pt.AccountID, CONVERT(datetime, pt.DueDate);

-- R2: invoice lines per instalment voucher. LinesPerTerm > 1 means the original @tabpay amount is multiplied by that factor.
SELECT TOP (500) 'R2 vouchers whose pay terms are joined to >1 invoice line' AS Section, p.Tag, p.VoucherNo, p.AccountId, p.DueDate,
       p.JoinedRows AS JoinedRows, p.PlanAmount_Original,
       (SELECT COUNT(DISTINCT pt2.DueDate) FROM PACT2C32..COM_DocPayTerms pt2 WITH(NOLOCK) WHERE pt2.VoucherNo = p.VoucherNo) AS DistinctDueDatesOnVoucher
FROM #plan p WHERE p.JoinedRows > 1 ORDER BY p.JoinedRows DESC, p.VoucherNo;

-- R3: identity ambiguity - one voucher, several tags / units.
SELECT TOP (500) 'R3a voucher with >1 tag' AS Section, VoucherNo, COUNT(DISTINCT Tag) AS Tags FROM #plan GROUP BY VoucherNo HAVING COUNT(DISTINCT Tag) > 1;
SELECT TOP (500) 'R3b voucher with >1 unit' AS Section, Tag, VoucherNo, COUNT(DISTINCT FlatUnit) AS Units, MIN(FlatUnit) AS FirstUnit, MAX(FlatUnit) AS LastUnit
FROM #invu GROUP BY Tag, VoucherNo HAVING COUNT(DISTINCT FlatUnit) > 1;

-- R4: same-date ties per tag (the cumulative DueDate <= allocation double-counts these).
SELECT TOP (500) 'R4 same-date instalments in one tag' AS Section, Tag, DueDate, COUNT(*) AS Rows_, SUM(PlanAmount_Original) AS PlanSum
FROM #plan GROUP BY Tag, DueDate HAVING COUNT(*) > 1 ORDER BY COUNT(*) DESC;

-- R5: VoucherNo is the only join key to COM_DocPayTerms; list pay-term vouchers that also exist in other cost centres' invoices.
SELECT TOP (500) 'R5 voucher number shared with another cost centre' AS Section, pt.VoucherNo, COUNT(DISTINCT inv2.CostCenterID) AS CostCenters
FROM PACT2C32..COM_DocPayTerms pt WITH(NOLOCK)
JOIN PACT2C32..Inv_DocDetails inv2 WITH(NOLOCK) ON inv2.VoucherNo = pt.VoucherNo
WHERE pt.AccountID IN (SELECT AccountID FROM #acct)
GROUP BY pt.VoucherNo HAVING COUNT(DISTINCT inv2.CostCenterID) > 1;

-- R6: ledger rows matched by BOTH join branches (AccDocDetailsID and InvDocDetailsID) are counted twice.
SELECT TOP (500) 'R6 ledger row joined to cost-centre data by both ids' AS Section, ACC.AccDocDetailsID, ACC.VoucherNo, ACC.Amount
FROM PACT2C32..ACC_DocDetails ACC WITH(NOLOCK)
JOIN PACT2C32..COM_DocCCData d1 WITH(NOLOCK) ON d1.AccDocDetailsID = ACC.AccDocDetailsID
JOIN PACT2C32..COM_DocCCData d2 WITH(NOLOCK) ON d2.InvDocDetailsID = ACC.InvDocDetailsID
WHERE ACC.StatusID IN (369, 429) AND (ACC.DebitAccount IN (SELECT AccountID FROM #acct) OR ACC.CreditAccount IN (SELECT AccountID FROM #acct));

-- R7: instalment plan total versus invoice total per voucher.
;WITH P AS (SELECT Tag, VoucherNo, SUM(PlanAmount_Original) AS PlanOriginal FROM #plan GROUP BY Tag, VoucherNo),
      V AS (SELECT Tag, VoucherNo, SUM(CONVERT(float, InvAmount)) AS InvTotal FROM #invu GROUP BY Tag, VoucherNo)
SELECT TOP (500) 'R7 plan total <> invoice total' AS Section, P.Tag, P.VoucherNo, P.PlanOriginal, V.InvTotal, P.PlanOriginal - V.InvTotal AS Diff
FROM P LEFT JOIN V ON V.Tag = P.Tag AND V.VoucherNo = P.VoucherNo WHERE ABS(P.PlanOriginal - ISNULL(V.InvTotal, 0)) > 0.005 ORDER BY ABS(P.PlanOriginal - ISNULL(V.InvTotal, 0)) DESC;

-- R8: plans whose tag has no ledger balance row (omitted by the original and by V2), and negative plan amounts.
SELECT TOP (500) 'R8a plan without ledger balance' AS Section, p.Tag, COUNT(*) AS PlanRows, SUM(p.PlanAmount_Original) AS PlanSum
FROM #plan p WHERE NOT EXISTS (SELECT 1 FROM #bal b WHERE CONVERT(varchar(20), b.Tag) = p.Tag) GROUP BY p.Tag;
SELECT TOP (500) 'R8b negative plan amount' AS Section, Tag, VoucherNo, DueDate, PlanAmount_Original FROM #plan WHERE PlanAmount_Original < 0;

-- R9: original vs V2 per tag for the reporting month window (create V2 first; skip if not deployed).
IF OBJECT_ID('dbo.p32AccountReceivablesV2') IS NOT NULL
BEGIN
    CREATE TABLE #v2 (CompanyID int,UnitCode varchar(120),TenantID varchar(120),FullName varchar(1200),Mobile varchar(200),Email varchar(200),UnitID bigint,VoucherNumber varchar(120),ChequeNumber varchar(50),DueDate datetime,Amount float,Status varchar(20),PaymentTermAccountId bigint,PlanAmount decimal(19,4),AllocatedAmount decimal(19,4));
    INSERT INTO #v2 EXEC dbo.p32AccountReceivablesV2 @StartDate = '2000-01-01', @EndDate = @MonthEnd, @MinAmount = 0, @IncludeSettled = 1, @StrictIdentity = 0;
    SELECT TOP (500) 'R9 per tag: original (all copies) vs V2' AS Section, COALESCE(o.TenantID, v.TenantID) AS TenantID,
           o.Rows_ AS OriginalRows, o.AmountSum AS OriginalAmountSum, v.Rows_ AS V2Rows, v.AmountSum AS V2AmountSum, ISNULL(o.AmountSum,0) - ISNULL(v.AmountSum,0) AS Diff
    FROM (SELECT TenantID, COUNT(*) Rows_, SUM(Amount) AmountSum FROM #orig GROUP BY TenantID) o
    FULL JOIN (SELECT TenantID, COUNT(*) Rows_, SUM(CONVERT(float, Amount)) AmountSum FROM #v2 GROUP BY TenantID) v ON v.TenantID = o.TenantID
    WHERE ABS(ISNULL(o.AmountSum,0) - ISNULL(v.AmountSum,0)) > 0.005 ORDER BY ABS(ISNULL(o.AmountSum,0) - ISNULL(v.AmountSum,0)) DESC;
END
ELSE SELECT 'R9 skipped: dbo.p32AccountReceivablesV2 is not deployed' AS Section;

-- R10: MinAmount = 0 - zero rows, float residue and the share of output that is settled.
SELECT 'R10 composition of the original output at MinAmount = 0' AS Section, COUNT(*) AS Rows_,
       SUM(CASE WHEN Amount = 0 THEN 1 ELSE 0 END) AS ZeroAmount,
       SUM(CASE WHEN Amount > 0 AND Amount < 0.005 THEN 1 ELSE 0 END) AS FloatResidue_lt_half_fils,
       SUM(CASE WHEN Amount >= 0.005 AND Amount < 1 THEN 1 ELSE 0 END) AS PositiveBelow1,
       SUM(CASE WHEN Amount >= 1 THEN 1 ELSE 0 END) AS AtLeast1,
       SUM(CASE WHEN DueDate >= @MonthStart AND DueDate <= @MonthEnd THEN 1 ELSE 0 END) AS InReportingMonth,
       SUM(CASE WHEN DueDate < @MonthStart THEN 1 ELSE 0 END) AS BeforeMonth
FROM #orig;
-- Timing comparison (run manually, one at a time; compare Elapsed and row counts):
--   SET STATISTICS TIME ON; EXEC dbo.p32AccountReceivables '2000-01-01', @MonthEnd, 0;  EXEC dbo.p32AccountReceivables '2000-01-01', @MonthEnd, 1;
-- The ledger scans do not depend on @MinAmount (it is applied only to the final SELECT), so the difference is result size/transfer.

-- R11: unit mapping. The original resolves the unit through COM_CC50010.Code = invoice unit NAME.
SELECT TOP (500) 'R11 invoice unit name has no matching code / duplicate code' AS Section, u.FlatUnit, COUNT(DISTINCT u.Tag) AS Tags,
       (SELECT COUNT(*) FROM PACT2C32..COM_CC50010 c WITH(NOLOCK) WHERE c.Code = u.FlatUnit) AS MatchingCodes
FROM #invu u GROUP BY u.FlatUnit HAVING (SELECT COUNT(*) FROM PACT2C32..COM_CC50010 c WITH(NOLOCK) WHERE c.Code = u.FlatUnit) <> 1;
