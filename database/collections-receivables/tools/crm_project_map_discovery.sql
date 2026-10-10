/*
  READ-ONLY discovery helper for dbo.CollectionsCrmProjectMap (V011). Run on TigerCsTicketing (10.10.10.117). It changes nothing and proposes nothing automatically.

  1. The CRM projects TigerCS has actually seen on tickets, and whether each already has a mapping.
  2. The PACT towers TigerCS knows (dbo.CollectionsTowers) - to be compared by a person with the CRM project; the name column is only a HINT for that person.
  After checking a project against CRM (tblProjects) and PACT, insert ONE verified row per project, e.g.
      INSERT dbo.CollectionsCrmProjectMap (CrmProjectId, ProjectCode, CompanyId, CrmProjectName, VerifiedBy, Note)
      VALUES (<crm project id>, N'TP<tower number>', NULL, N'<CRM project name>', N'<who verified>', N'<how it was verified>');
  CompanyId stays NULL unless the tower number exists in both companies (then it MUST be given).
*/
SET NOCOUNT ON;

SELECT t.CrmBuyerProjectId AS CrmProjectId, MAX(t.CrmBuyerProjectName) AS CrmProjectName, COUNT(*) AS Tickets,
       (SELECT COUNT(*) FROM dbo.CollectionsCrmProjectMap m WHERE m.CrmProjectId = t.CrmBuyerProjectId AND m.IsActive = 1) AS ActiveMappings
  FROM dbo.Tickets t
 WHERE t.CrmBuyerProjectId IS NOT NULL
 GROUP BY t.CrmBuyerProjectId
 ORDER BY ActiveMappings, Tickets DESC;

SELECT CompanyId, TowerNumber, TowerName, IsActive,
       (SELECT COUNT(DISTINCT c.CompanyId) FROM dbo.CollectionsTowers c WHERE LTRIM(RTRIM(CONVERT(nvarchar(20), c.TowerNumber))) = LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber)))) AS CompaniesWithThisTowerNumber
  FROM dbo.CollectionsTowers t
 ORDER BY TowerNumber, CompanyId;
