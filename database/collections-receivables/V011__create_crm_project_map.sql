/*
  V011 - CRM project -> PACT tower mapping (TigerCsTicketing).

  GetBuyerByPhone / GetUnitDetails identify a unit's project by CRM project id and display name only - no PACT code. To read a customer's PACT receivables by tower + apartment the
  application therefore needs a VERIFIED link  CRM project id -> PACT project code (TP140).  Nothing in this repository proves such a link, so none is invented here:

    dbo.CollectionsCrmProjectMap            table  one row per verified (CRM project id, PACT project code[, PACT company]); filled by an operator after checking it against CRM and PACT
    dbo.usp_Collections_GetCrmProjectMap    proc   rows for a JSON list of CRM project ids = the MANUAL rows plus, for projects without a manual row, the (project id, project code) pairs
                                                   of the CURRENT CRM owner feed run (V010) - the feed carries both, so it maps projects by itself once CRM publishes GetUnitOwners.

  A project with no row is reported by the application as ProjectMappingMissing (no amounts are looked up); a project with several codes as ProjectMappingAmbiguous.
  Idempotent. Needs V010 (the procedure reads CollectionsCrmOwnerState / CollectionsCrmUnitOwner). Does NOT insert any mapping row.
*/
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'dbo.CollectionsCrmProjectMap', N'U') IS NULL
CREATE TABLE dbo.CollectionsCrmProjectMap
(
    MapId         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionsCrmProjectMap PRIMARY KEY,
    CrmProjectId  int            NOT NULL,
    ProjectCode   nvarchar(50)   NOT NULL,         -- the PACT unit-code prefix of the tower, e.g. TP140 (unit code = ProjectCode + '-' + apartment)
    CompanyId     int            NULL,             -- the PACT company when the mapping itself names it; NULL = decided by dbo.CollectionsTowers (a tower in both companies stays ambiguous)
    CrmProjectName nvarchar(400) NULL,             -- for the operator only; never used for matching
    VerifiedBy    nvarchar(200)  NOT NULL,
    VerifiedAtUtc datetime2(3)   NOT NULL CONSTRAINT DF_CollectionsCrmProjectMap_Verified DEFAULT (SYSUTCDATETIME()),
    Note          nvarchar(400)  NULL,
    IsActive      bit            NOT NULL CONSTRAINT DF_CollectionsCrmProjectMap_Active DEFAULT (1)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsCrmProjectMap') AND name = N'UX_CollectionsCrmProjectMap_Project_Code')
    CREATE UNIQUE INDEX UX_CollectionsCrmProjectMap_Project_Code ON dbo.CollectionsCrmProjectMap (CrmProjectId, ProjectCode) WHERE IsActive = 1;
GO
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCrmProjectMap @Json nvarchar(max)      -- @Json = [79, 80, ...] (CRM project ids)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @p TABLE (ProjectId int NOT NULL PRIMARY KEY);
    INSERT @p (ProjectId) SELECT DISTINCT TRY_CAST([value] AS int) FROM OPENJSON(@Json) WHERE TRY_CAST([value] AS int) IS NOT NULL;

    SELECT m.CrmProjectId, m.ProjectCode, m.CompanyId, CAST('Manual' AS varchar(10)) AS Source
      FROM dbo.CollectionsCrmProjectMap m JOIN @p p ON p.ProjectId = m.CrmProjectId
     WHERE m.IsActive = 1
    UNION ALL
    SELECT o.ProjectId, o.ProjectCode,
           CASE WHEN COUNT(DISTINCT o.CompanyId) = 1 AND SUM(CASE WHEN o.CompanyId IS NULL THEN 1 ELSE 0 END) = 0 THEN MIN(o.CompanyId) END,
           CAST('CrmFeed' AS varchar(10))
      FROM dbo.CollectionsCrmOwnerState s
      JOIN dbo.CollectionsCrmUnitOwner o ON o.RunId = s.CurrentRunId
      JOIN @p p ON p.ProjectId = o.ProjectId
     WHERE s.Id = 1 AND o.ProjectCode <> N''
       AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsCrmProjectMap m WHERE m.CrmProjectId = o.ProjectId AND m.IsActive = 1)
     GROUP BY o.ProjectId, o.ProjectCode;
END;
GO
