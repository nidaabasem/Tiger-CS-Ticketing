/*
  diagnose_crm_unit.sql - READ-ONLY. Why does a unit show no CRM name / mobile / e-mail?   Set @UnitKey and run (changes nothing).

  It follows the exact path a unit takes from CRM to the pages and says where it stops:
    1. the CRM feed itself (loaded? how old? last error? how many sales were left out and why - the LastNote),
    2. the stored owner rows of this unit (lead, status, buyer, raw and normalised mobile, e-mail, CRM project, the company they were tied to and HOW),
    3. the tower in dbo.CollectionsTowers (a missing / double / placeholder row is what left TP119 nameless and its owners untied before V012),
    4. the PACT accounts that hold the unit (company, tenant, PACT mobile),
    5. the link the pages read, and a VERDICT (one row) naming the cause.
  Example: @UnitKey = N'TP115-1921'  (Blue Wave, lead 256146).
*/
SET NOCOUNT ON;
DECLARE @UnitKey nvarchar(220) = N'TP115-1921';

SET @UnitKey = dbo.fn_CollectionsUnitKey(@UnitKey);
DECLARE @tower nvarchar(20) = dbo.fn_CollectionsTowerNumber(@UnitKey);
DECLARE @run uniqueidentifier = (SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1);
PRINT CONCAT(N'Unit ', @UnitKey, N', tower ', @tower);

PRINT N'--- 1. CRM feed state ---';
SELECT CAST(CASE WHEN CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS Loaded, LastSuccessUtc, DATEDIFF(MINUTE, LastSuccessUtc, SYSUTCDATETIME()) AS AgeMinutes, LastAttemptUtc, LastAttemptStatus, LastError,
       RowCount_ AS StoredOwners, UnlinkedRows, AmbiguousRows, LastNote
  FROM dbo.CollectionsCrmOwnerState WHERE Id = 1;

PRINT N'--- 2. Stored CRM owner rows of this unit (current run) ---';
SELECT o.LeadId, o.LeadStatus, o.CrmCustomerId, o.FullName, o.Mobile, o.PhoneNorm, o.Email, o.ProjectId, o.ProjectCode, o.ProjectName, o.CompanyId, o.CompanySource
  INTO #owners
  FROM dbo.CollectionsCrmUnitOwner o WHERE o.RunId = @run AND o.UnitKey = @UnitKey COLLATE Latin1_General_BIN2;
SELECT LeadId, LeadStatus, CrmCustomerId, FullName, Mobile AS CrmMobileRaw, PhoneNorm AS CrmMobileNormalised, Email, ProjectId, ProjectCode, ProjectName, CompanyId, CompanySource FROM #owners;

PRINT N'--- 3. dbo.CollectionsTowers rows for the tower number ---';
SELECT t.TowerId, t.CompanyId, CONVERT(nvarchar(400), t.TowerName) AS TowerName, t.IsActive,
       CASE WHEN LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName))) = N'' OR UPPER(LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName)))) IN (UPPER(@tower), N'TP' + UPPER(@tower), N'TP-' + UPPER(@tower))
            THEN 'placeholder (blank or only the number)' ELSE 'real name' END AS NameKind
  FROM dbo.CollectionsTowers t WHERE LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = @tower;
SELECT n.CompanyId, n.TowerNumber, n.TowerName, n.NameSource FROM dbo.fn_Collections_TowerNames() n WHERE n.TowerNumber = @tower;

PRINT N'--- 4. PACT accounts that hold the unit (current snapshot) ---';
SELECT s.CompanyId, s.TenantId, s.UnitId, s.UnitCode, MAX(s.FullName) AS PactName, MAX(s.Mobile) AS PactMobile, MAX(s.Email) AS PactEmail, COUNT(*) AS UnpaidRows
  INTO #pact
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL AND dbo.fn_CollectionsUnitKey(s.UnitCode) = @UnitKey COLLATE Latin1_General_BIN2
 GROUP BY s.CompanyId, s.TenantId, s.UnitId, s.UnitCode;
SELECT * FROM #pact;

PRINT N'--- 5. The link the pages read (per company) ---';
SELECT l.CompanyId, l.Customers, l.CustomerId, l.FullName, l.PhoneNorm, l.EmailNorm, l.CompanyAmbiguous FROM dbo.fn_Collections_CrmUnitLinks() l WHERE l.UnitKey = @UnitKey COLLATE Latin1_General_BIN2;

PRINT N'--- VERDICT ---';
SELECT TOP (1) Cause, Explanation FROM
(
    SELECT 1 AS Ord, 'CRM_FEED_NOT_LOADED' AS Cause, N'The CRM owner feed has never been stored (CollectionsSource:CrmOwners:Enabled off, GetUnitOwners not published, or every load failed - see LastError / LastNote above). No unit has CRM data.' AS Explanation
      WHERE @run IS NULL
    UNION ALL SELECT 2, 'CRM_FEED_STALE', N'The last successful CRM load is older than the allowed age; the pages flag every unit CrmUnavailable.'
      WHERE @run IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.CollectionsCrmOwnerState WHERE Id = 1 AND DATEDIFF(MINUTE, LastSuccessUtc, SYSUTCDATETIME()) > 360)
    UNION ALL SELECT 3, 'NO_OWNER_ROW', N'No eligible CRM sale is stored for this unit key. Either CRM has no Sold/Contract buyer lead for it, or the feed row was left out (see LastNote: NoProjectCode / NotBuyer / NotSoldOrContractOrCancelled / UnitZeroOrCancelled), or the CRM project code + unit number do not form this key (compare ProjectCode with the PACT code, or add a verified row to dbo.CollectionsCrmProjectMap).'
      WHERE @run IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #owners)
    UNION ALL SELECT 4, 'OWNER_COMPANY_AMBIGUOUS', N'The unit code is held by PACT accounts of BOTH companies, so the CRM sale is tied to neither (nobody is guessed). The unit is flagged "needs review".'
      WHERE EXISTS (SELECT 1 FROM #owners WHERE CompanySource = 'Ambiguous')
    UNION ALL SELECT 5, 'OWNER_WITHOUT_COMPANY', N'The CRM sale exists but no company could be chosen: the tower is not (uniquely) in dbo.CollectionsTowers and no PACT snapshot holds this exact unit code. It links as soon as a snapshot holds the unit (re-run usp_Collections_ResolveCrmOwnerCompanies).'
      WHERE EXISTS (SELECT 1 FROM #owners WHERE CompanyId IS NULL AND ISNULL(CompanySource, '') <> 'Ambiguous')
    UNION ALL SELECT 6, 'SEVERAL_CRM_BUYERS', N'More than one distinct CRM customer holds an eligible sale of this unit: nobody is chosen, PACT keeps its own contact, the unit is flagged.'
      WHERE (SELECT COUNT(DISTINCT CrmCustomerId) FROM #owners WHERE CompanyId IS NOT NULL) > 1
    UNION ALL SELECT 7, 'CRM_MOBILE_MISSING_IN_CRM', N'The tied CRM buyer has no mobile number in CRM at all (CrmMobileRaw is empty).'
      WHERE EXISTS (SELECT 1 FROM #owners WHERE CompanyId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM #owners WHERE CompanyId IS NOT NULL AND LTRIM(RTRIM(Mobile)) <> N'')
    UNION ALL SELECT 8, 'CRM_MOBILE_NOT_A_VALID_NUMBER', N'CRM holds a mobile but the application could not confirm it as an international number (CrmMobileNormalised is empty): wrong length, letters, or two numbers in one field.'
      WHERE EXISTS (SELECT 1 FROM #owners WHERE CompanyId IS NOT NULL AND LTRIM(RTRIM(Mobile)) <> N'' AND PhoneNorm = N'')
    UNION ALL SELECT 9, 'LINKED_OK', N'The CRM owner is tied to the unit''s company with a valid mobile: the pages show it. If a page still shows no mobile, the page is older than this data (reload) or a contact conflict with PACT is flagged (see the review reason).'
      WHERE EXISTS (SELECT 1 FROM #owners WHERE CompanyId IS NOT NULL AND PhoneNorm <> N'')
) v ORDER BY Ord;
