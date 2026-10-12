/*
  verify_account_directory.sql - READ-ONLY (TigerCsTicketing). Run BEFORE switching ReceivablesSnapshot:AccountDirectoryComplete on.
  The unit/account directory (CollectionsRcvAccountUnit) holds what PACT's receivables procedure returned, fully paid units included. It is complete scope evidence for the payment statement
  only if it lists EVERY unit of a tenant account. This script gives the figures and sample tenants to compare with PACT/EDSM by hand:
    for each sample tenant, GET v1/contracts/{mobile} (PACT) must list exactly the units shown here - no more (e.g. a parking contract, a unit without a payment plan), no fewer.
  If any sample differs, leave the switch OFF and ask PACT for the tenant-to-units query named in the application's refusal text.
*/
SET NOCOUNT ON;
SELECT 'directory_by_company' AS section, c.CompanyId, c.LastSuccessUtc,
       COUNT(*) AS Units, COUNT(DISTINCT a.TenantId) AS Tenants, SUM(CASE WHEN a.OpenRows = 0 THEN 1 ELSE 0 END) AS FullyPaidUnits, SUM(CAST(a.IsCancelled AS int)) AS CancelledUnits
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvAccountUnit a ON a.CompanyId = c.CompanyId AND a.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL GROUP BY c.CompanyId, c.LastSuccessUtc;

/* Tenants with several units, at least one fully paid: the case an open-balance check misses. Compare each with PACT's contract list. */
SELECT TOP 20 'sample_multi_unit_tenants' AS section, a.CompanyId, a.TenantId, COUNT(*) AS Units, SUM(CASE WHEN a.OpenRows = 0 THEN 1 ELSE 0 END) AS FullyPaidUnits,
       STRING_AGG(a.UnitCode, N', ') WITHIN GROUP (ORDER BY a.UnitId) AS UnitCodes
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvAccountUnit a ON a.CompanyId = c.CompanyId AND a.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL GROUP BY a.CompanyId, a.TenantId HAVING COUNT(*) > 1 AND SUM(CASE WHEN a.OpenRows = 0 THEN 1 ELSE 0 END) > 0
 ORDER BY COUNT(*) DESC;

/* Single-unit tenants that are fully paid: compare a few with PACT - if PACT lists more contracts for them, the directory is NOT complete. */
SELECT TOP 20 'sample_single_paid_units' AS section, a.CompanyId, a.TenantId, a.UnitCode
  FROM dbo.CollectionsRcvCompany c JOIN dbo.CollectionsRcvAccountUnit a ON a.CompanyId = c.CompanyId AND a.RunId = c.CurrentRunId
 WHERE c.CurrentRunId IS NOT NULL AND a.OpenRows = 0
   AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsRcvAccountUnit b WHERE b.RunId = a.RunId AND b.CompanyId = a.CompanyId AND b.TenantId = a.TenantId AND b.UnitId <> a.UnitId)
 ORDER BY NEWID();
