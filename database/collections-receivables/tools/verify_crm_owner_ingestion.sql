/*
  verify_crm_owner_ingestion.sql - READ-ONLY (TigerCsTicketing). Run AFTER deployment, once the job collections-crm-owners-refresh has completed at least once
  (or after GET /api/collections/crm-owners/status shows Ready). It checks that the published CRM owner feed (GetUnitOwners, total = 10086 at the time of writing) reached the
  local tables and the joins intact. Nothing is changed. Compare the figures with the status endpoint (fetched / stored / rejected by reason).
*/
SET NOCOUNT ON;
DECLARE @run uniqueidentifier = (SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1);

SELECT '1_feed_state' AS section, CASE WHEN CurrentRunId IS NULL THEN 'NOT LOADED' ELSE 'Loaded' END AS Loaded, LastSuccessUtc, DATEDIFF(MINUTE, LastSuccessUtc, SYSUTCDATETIME()) AS AgeMinutes,
       LastAttemptStatus, LastError, RowCount_ AS StoredOwners, UnlinkedRows, AmbiguousRows, LastNote
  FROM dbo.CollectionsCrmOwnerState WHERE Id = 1;

/* 2. Only Sold (8) and Contract (9) may be stored: Approved (4) and Cancelled (6) must never appear. */
SELECT '2_by_lead_status' AS section, LeadStatus, COUNT(*) AS Owners FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @run GROUP BY LeadStatus ORDER BY LeadStatus;
SELECT '2b_VIOLATION_ineligible_status' AS section, COUNT(*) AS Owners FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @run AND LeadStatus NOT IN (8, 9);

/* 3. Linking quality: rows tied to a company, rows left unlinked / ambiguous, rows without a mobile or e-mail. */
SELECT '3_linking' AS section,
       SUM(CASE WHEN CompanyId IS NOT NULL THEN 1 ELSE 0 END) AS TiedToCompany,
       SUM(CASE WHEN CompanyId IS NULL THEN 1 ELSE 0 END) AS NoCompany,
       SUM(CASE WHEN PhoneNorm = N'' THEN 1 ELSE 0 END) AS NoValidMobile,
       SUM(CASE WHEN EmailNorm = N'' THEN 1 ELSE 0 END) AS NoEmail,
       SUM(CASE WHEN UnitKey LIKE N'%*%' THEN 1 ELSE 0 END) AS VIOLATION_StarredKeys
  FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @run;
SELECT '3b_company_source' AS section, CompanySource, COUNT(*) AS Owners FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @run GROUP BY CompanySource;

/* 4. Do the keys (projectCode-unitNumber) meet PACT's unit codes? Owners whose key is in NO company's unit/account directory cannot be linked to a PACT unit. */
SELECT DISTINCT UPPER(LTRIM(RTRIM(a.UnitCode))) COLLATE Latin1_General_BIN2 AS UnitKey
  INTO #pact
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvAccountUnit a ON a.CompanyId = c.CompanyId AND a.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL AND a.IsCancelled = 0;
SELECT o.UnitKey, o.ProjectCode, o.ProjectName, o.LeadId, o.LeadStatus, CASE WHEN p.UnitKey IS NULL THEN 0 ELSE 1 END AS InPact
  INTO #owners
  FROM dbo.CollectionsCrmUnitOwner o LEFT JOIN #pact p ON p.UnitKey = o.UnitKey
 WHERE o.RunId = @run;
SELECT '4_owner_keys_vs_pact_directory' AS section, COUNT(*) AS Owners, SUM(InPact) AS KeyFoundInPact, SUM(1 - InPact) AS KeyNotInPact FROM #owners;
SELECT TOP 25 '4b_sample_keys_not_in_pact' AS section, UnitKey, ProjectCode, ProjectName, LeadId, LeadStatus FROM #owners WHERE InPact = 0 ORDER BY UnitKey;
DROP TABLE #owners; DROP TABLE #pact;

/* 5. The reported unit: TP140-407 / lead 307092. For a full trace use tools/diagnose_crm_unit.sql with @UnitKey = N'TP140-407'. */
SELECT '5_TP140-407' AS section, o.LeadId, o.LeadStatus, o.CrmCustomerId, o.FullName, o.Mobile, o.PhoneNorm, o.Email, o.ProjectCode, o.ProjectName, o.CompanyId, o.CompanySource
  FROM dbo.CollectionsCrmUnitOwner o WHERE o.RunId = @run AND o.UnitKey = N'TP140-407' COLLATE Latin1_General_BIN2;
