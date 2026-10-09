/*
  DEPLOY p32AccountReceivablesV2  (company 32)                                                    -- order: see deploy/README.md
  Creates the NEW, versioned procedure dbo.p32AccountReceivablesV2. The deployed dbo.p32AccountReceivables is NOT touched, so the
  application keeps working until CollectionsSource:PactReceivables:ProcedureSuffix is set to "V2".
  Run in the SAME database that already holds dbo.p32AccountReceivables (the database the PACTRPT connection string points to),
  after deploy/00-preflight.sql. Re-runnable: the first batch creates an empty stub only if the procedure is missing, the second
  batch (ALTER) replaces the body. Rollback: deploy/90-rollback.sql.
  The body below is byte-for-byte the reviewed draft p32AccountReceivablesV2.review.sql (enforced by PactDeployScriptsTests),
  with only the leading CREATE changed to ALTER. It has not been executed against SQL Server by the author.
*/
IF OBJECT_ID(N'dbo.p32AccountReceivablesV2', N'P') IS NULL
    EXEC (N'CREATE PROCEDURE dbo.p32AccountReceivablesV2 AS BEGIN SET NOCOUNT ON; SELECT 1 AS Stub; END');
GO
ALTER PROCEDURE [dbo].[p32AccountReceivablesV2]
    @StartDate datetime,
    @EndDate datetime,
    @MinAmount decimal(19, 4) = 0,
    @IncludeSettled bit = 0,
    @StrictIdentity bit = 1
AS
BEGIN
SET NOCOUNT ON;

DECLARE @From FLOAT = CONVERT(FLOAT, CONVERT(DATETIME, '01-01-2000')),
        @To FLOAT = CONVERT(FLOAT, CONVERT(DATETIME, '01-01-2099'));

IF OBJECT_ID('tempdb..#Accounts') IS NOT NULL DROP TABLE #Accounts;
IF OBJECT_ID('tempdb..#InvoiceUnit') IS NOT NULL DROP TABLE #InvoiceUnit;
IF OBJECT_ID('tempdb..#AccountBalance') IS NOT NULL DROP TABLE #AccountBalance;
IF OBJECT_ID('tempdb..#Instalment') IS NOT NULL DROP TABLE #Instalment;
IF OBJECT_ID('tempdb..#TagPaid') IS NOT NULL DROP TABLE #TagPaid;

-- Receivable account tree (unchanged scope: group 4010).
SELECT DISTINCT T.AccountID INTO #Accounts
FROM PACT2C32..ACC_Accounts T WITH(NOLOCK), PACT2C32..ACC_Accounts GT WITH(NOLOCK)
WHERE T.lft BETWEEN GT.lft AND GT.rgt AND GT.AccountID IN (4010);

-- Invoice totals per (Tag, invoice voucher, unit, pay mode). Same source as the original #tabInv.
SELECT CONVERT(varchar(20), dcc.dcCCNID7) TagInv, inv.VoucherNo SInvNo, SUM(DCN.dcNum6) InvAmount, f.Name FlatUnit, DCT.dcAlpha5 PayMode
INTO #InvoiceUnit
FROM PACT2C32..Inv_DocDetails Inv WITH(NOLOCK)
INNER JOIN PACT2C32..COM_DocCCData   DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_DocTextData DCT WITH(NOLOCK) ON DCT.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_DocNumData  DCN WITH(NOLOCK) ON DCN.InvDocDetailsID=Inv.InvDocDetailsID
INNER JOIN PACT2C32..COM_CC50010 f WITH(NOLOCK) ON dcc.dcCCNID10=f.NodeID
WHERE inv.CostCenterID=41013 AND inv.StatusId=369
GROUP BY dcc.dcCCNID7, inv.VoucherNo, f.Name, DCT.dcAlpha5;

-- Ledger balance per (Account, Tag): the original ledger block, unchanged (8 UNION ALL branches).
SELECT A1.AccountID, ACC.TagID Tag, ((OP_Dr + TR_Dr) - (OP_Cr + TR_Cr)) Balance
INTO #AccountBalance
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
WHERE A1.AccountID>1 AND A1.AccountID IN (SELECT AccountID FROM #Accounts) AND ACC.TagID IS NOT NULL;

-- Instalments: one row per (voucher, Tag) first, THEN the pay terms, so invoice lines cannot multiply the amounts.
;WITH V AS (
    SELECT inv.VoucherNo, CONVERT(varchar(20), dcc.dcCCNID7) Tag
    FROM PACT2C32..Inv_DocDetails Inv WITH(NOLOCK)
    INNER JOIN PACT2C32..COM_DocCCData   DCC WITH(NOLOCK) ON DCC.InvDocDetailsID=Inv.InvDocDetailsID
    INNER JOIN PACT2C32..COM_DocTextData DCT WITH(NOLOCK) ON DCT.InvDocDetailsID=Inv.InvDocDetailsID
    WHERE inv.CostCenterID=41013 AND inv.StatusId=369 AND UPPER(DCT.dcAlpha5)=UPPER('Installment')
    GROUP BY inv.VoucherNo, dcc.dcCCNID7
)
SELECT v.Tag, pt.VoucherNo, CONVERT(bigint, pt.AccountID) AccountId, CONVERT(datetime, pt.DueDate) DueDate,
       CONVERT(decimal(19,4), ROUND(SUM(pt.Amount), 4)) PlanAmount
INTO #Instalment
FROM V v
INNER JOIN PACT2C32..COM_DocPayTerms pt WITH(NOLOCK) ON pt.VoucherNo=v.VoucherNo
WHERE pt.AccountID IN (SELECT AccountID FROM #Accounts)
GROUP BY v.Tag, pt.VoucherNo, pt.AccountID, CONVERT(datetime, pt.DueDate);

IF @StrictIdentity = 1
BEGIN
    IF EXISTS (SELECT 1 FROM #Instalment GROUP BY VoucherNo HAVING COUNT(DISTINCT Tag) > 1)
        THROW 51001, 'A payment-term voucher maps to more than one Tag; run the reconciliation queries (R3) before using this procedure.', 1;
    IF EXISTS (SELECT 1 FROM #Instalment i JOIN #InvoiceUnit u ON u.TagInv=i.Tag AND u.SInvNo=i.VoucherNo GROUP BY i.VoucherNo, i.Tag HAVING COUNT(DISTINCT u.FlatUnit) > 1)
        THROW 51002, 'A payment-term voucher maps to more than one unit; run the reconciliation queries (R3) before using this procedure.', 1;
END;

-- Tag-level paid amount, each invoice and each account balance counted once. Tags without any ledger row are not reported
-- (same as the original, where the tag came from the ledger side); see reconciliation query R8.
SELECT inv.Tag, CONVERT(decimal(19,4), ROUND(inv.InvTotal - bal.BalTotal, 4)) PaidAmount
INTO #TagPaid
FROM (SELECT TagInv Tag, SUM(CONVERT(float, InvAmount)) InvTotal FROM #InvoiceUnit GROUP BY TagInv) inv
INNER JOIN (SELECT CONVERT(varchar(20), Tag) Tag, SUM(Balance) BalTotal FROM #AccountBalance GROUP BY CONVERT(varchar(20), Tag)) bal ON bal.Tag=inv.Tag;

-- ===== allocation core (identical text is unit-tested in TigerCS.Tests: PactAllocationCoreTests) =====
;WITH Instalment AS (SELECT Tag, VoucherNo, AccountId, DueDate, PlanAmount FROM #Instalment),
TagPaid AS (SELECT Tag, PaidAmount FROM #TagPaid),
Allocated AS (
    SELECT i.Tag, i.VoucherNo, i.AccountId, i.DueDate, i.PlanAmount,
           CASE WHEN COALESCE(p.PaidAmount, 0) <= i.CumBefore THEN 0
                WHEN COALESCE(p.PaidAmount, 0) - i.CumBefore >= i.PlanAmount THEN i.PlanAmount
                ELSE COALESCE(p.PaidAmount, 0) - i.CumBefore END AS AllocatedAmount
    FROM (SELECT Tag, VoucherNo, AccountId, DueDate, PlanAmount,
                 COALESCE(SUM(PlanAmount) OVER (PARTITION BY Tag ORDER BY DueDate, VoucherNo, AccountId
                          ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS CumBefore
          FROM Instalment) i
    LEFT JOIN TagPaid p ON p.Tag = i.Tag
)
SELECT 32 AS CompanyID,
       
       'TP' + udet.Code AS UnitCode,
       al.Tag AS TenantID,
       b.[Name] AS FullName,
       contc.ccAlpha9 AS Mobile,
       contc.ccAlpha11 AS Email,
       udet.NodeID AS UnitID,
       al.VoucherNo AS VoucherNumber,
       '' AS ChequeNumber,
       al.DueDate AS DueDate,
       CONVERT(decimal(19,4), al.PlanAmount - al.AllocatedAmount) AS Amount,
       CASE WHEN al.PlanAmount - al.AllocatedAmount = 0 THEN 'Paid' ELSE 'Installment' END AS Status,
       al.AccountId AS PaymentTermAccountId,
       al.PlanAmount AS PlanAmount,
       al.AllocatedAmount AS AllocatedAmount
FROM Allocated al
INNER JOIN TagPaid tp ON tp.Tag = al.Tag
LEFT JOIN (SELECT NodeID, Code, Name FROM PACT2C32..COM_Area WITH(NOLOCK) WHERE (IsGroup=0 OR NodeID=1)) b ON b.NodeID = TRY_CONVERT(bigint, al.Tag)
LEFT JOIN (SELECT NodeID, ccAlpha9, ccAlpha11 FROM PACT2C32..COM_Area WITH(NOLOCK)) contc ON contc.NodeID = TRY_CONVERT(bigint, al.Tag)
OUTER APPLY (SELECT TOP 1 u.FlatUnit FROM #InvoiceUnit u WHERE u.TagInv = al.Tag AND u.SInvNo = al.VoucherNo ORDER BY u.FlatUnit) iu
OUTER APPLY (SELECT TOP 1 c.Code, c.NodeID FROM PACT2C32..COM_CC50010 c WITH(NOLOCK) WHERE c.Code = iu.FlatUnit ORDER BY c.NodeID) udet
WHERE al.DueDate BETWEEN @StartDate AND @EndDate
  AND (al.PlanAmount - al.AllocatedAmount) >= @MinAmount
  AND (@IncludeSettled = 1 OR (al.PlanAmount - al.AllocatedAmount) > 0)
ORDER BY al.DueDate DESC, al.Tag, al.VoucherNo, al.AccountId;
END
GO
