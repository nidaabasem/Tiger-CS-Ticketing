/*
  V013 - Evidence for the CRM project -> PACT tower mapping (TigerCsTicketing).

  A CRM project (tblProjects.ID, e.g. 94 'VOLGA Tower') has no PACT code in the CRM lookups (GetBuyerByPhone / GetUnitDetails). Names are never used to build the link.
  When a customer is looked up, the application compares the CRM unit numbers of that customer with the apartment numbers of the SAME customer's PACT contracts; when exactly one
  active contract matches, it records one observation here:   CRM project id -> (PACT project code, company, tenant, PACT unit code).

    dbo.CollectionsCrmProjectMapEvidence      table  one row per distinct observation (CrmProjectId, ProjectCode, CompanyId, CrmUnitId); ObservedCount/LastObservedUtc grow on repeats
    dbo.usp_Collections_RecordCrmProjectEvidence  proc  upserts a JSON array of observations; unit codes containing '*' (cancelled in PACT) are NEVER recorded

  This table NEVER creates a mapping. tools/review_crm_project_map.sql previews it (mapped / evidence-consistent / ambiguous / unresolved) and, only when run with @Apply = 1,
  promotes the projects whose evidence names exactly one (ProjectCode, CompanyId) into dbo.CollectionsCrmProjectMap with VerifiedBy = the operator you pass.
  Idempotent. Needs V011.
*/
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'dbo.CollectionsCrmProjectMapEvidence', N'U') IS NULL
CREATE TABLE dbo.CollectionsCrmProjectMapEvidence
(
    EvidenceId       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionsCrmProjectMapEvidence PRIMARY KEY,
    CrmProjectId     int            NOT NULL,
    ProjectCode      nvarchar(50)   NOT NULL,
    CompanyId        int            NOT NULL,
    CrmUnitId        int            NOT NULL,
    CrmCustomerId    int            NOT NULL,
    UnitNumber       nvarchar(100)  NOT NULL,
    PactTenantId     nvarchar(100)  NOT NULL,
    PactUnitCode     nvarchar(200)  NOT NULL,
    ObservedCount    int            NOT NULL CONSTRAINT DF_CollectionsCrmProjectMapEvidence_Cnt DEFAULT (1),
    FirstObservedUtc datetime2(3)   NOT NULL CONSTRAINT DF_CollectionsCrmProjectMapEvidence_First DEFAULT (SYSUTCDATETIME()),
    LastObservedUtc  datetime2(3)   NOT NULL CONSTRAINT DF_CollectionsCrmProjectMapEvidence_Last DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsCrmProjectMapEvidence') AND name = N'UX_CollectionsCrmProjectMapEvidence_Obs')
    CREATE UNIQUE INDEX UX_CollectionsCrmProjectMapEvidence_Obs ON dbo.CollectionsCrmProjectMapEvidence (CrmProjectId, ProjectCode, CompanyId, CrmUnitId);
GO
CREATE OR ALTER PROCEDURE dbo.usp_Collections_RecordCrmProjectEvidence @Json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT j.ProjectId, LTRIM(RTRIM(j.ProjectCode)) AS ProjectCode, j.CompanyId, j.CrmUnitId, j.CrmCustomerId, j.UnitNumber, j.TenantId, j.PactUnitCode
      INTO #e
      FROM OPENJSON(@Json) WITH (ProjectId int '$.projectId', ProjectCode nvarchar(50) '$.projectCode', CompanyId int '$.companyId', CrmUnitId int '$.crmUnitId',
                                 CrmCustomerId int '$.crmCustomerId', UnitNumber nvarchar(100) '$.unitNumber', TenantId nvarchar(100) '$.tenantId', PactUnitCode nvarchar(200) '$.pactUnitCode') j
     WHERE j.ProjectId IS NOT NULL AND j.CompanyId IS NOT NULL AND j.CrmUnitId IS NOT NULL AND ISNULL(LTRIM(RTRIM(j.ProjectCode)), N'') <> N''
       AND ISNULL(j.PactUnitCode, N'') NOT LIKE N'%*%';          -- a cancelled PACT unit proves nothing

    UPDATE v SET ObservedCount = v.ObservedCount + 1, LastObservedUtc = SYSUTCDATETIME()
      FROM dbo.CollectionsCrmProjectMapEvidence v
      JOIN #e e ON e.ProjectId = v.CrmProjectId AND e.ProjectCode = v.ProjectCode AND e.CompanyId = v.CompanyId AND e.CrmUnitId = v.CrmUnitId;

    INSERT dbo.CollectionsCrmProjectMapEvidence (CrmProjectId, ProjectCode, CompanyId, CrmUnitId, CrmCustomerId, UnitNumber, PactTenantId, PactUnitCode)
    SELECT e.ProjectId, e.ProjectCode, e.CompanyId, e.CrmUnitId, MIN(e.CrmCustomerId), MIN(ISNULL(e.UnitNumber, N'')), MIN(ISNULL(e.TenantId, N'')), MIN(ISNULL(e.PactUnitCode, N''))
      FROM #e e
     WHERE NOT EXISTS (SELECT 1 FROM dbo.CollectionsCrmProjectMapEvidence v
                        WHERE v.CrmProjectId = e.ProjectId AND v.ProjectCode = e.ProjectCode AND v.CompanyId = e.CompanyId AND v.CrmUnitId = e.CrmUnitId)
     GROUP BY e.ProjectId, e.ProjectCode, e.CompanyId, e.CrmUnitId;
END;
GO
