/*
  review_crm_project_map.sql - preview and (optionally) repair the CRM project -> PACT tower map (TigerCsTicketing). Needs V011 and V013.

  READ-ONLY by default (@Apply = 0): it returns
    1. every CRM project seen in the evidence with its state:
         Mapped              an active dbo.CollectionsCrmProjectMap row exists
         ReadyToPromote      no map row; the evidence names exactly ONE (ProjectCode, CompanyId)
         Ambiguous           no map row; the evidence names more than one (ProjectCode, CompanyId) - never promoted, resolve by hand
         Unresolved          the CRM feed (V010) saw the project but no evidence exists yet - nothing to promote
    2. the evidence rows behind each project.
  With @Apply = 1 it inserts ONE dbo.CollectionsCrmProjectMap row per ReadyToPromote project (idempotent: re-running adds nothing). Nothing is ever guessed from names, and no project id,
  tower or company is hardcoded here. Check a ReadyToPromote row against CRM/PACT before you apply.

  Example (project 94 'VOLGA Tower'): it becomes ReadyToPromote only if customers' own PACT contracts proved a single tower/company. The values 94 -> Tower 125 -> Company 4 are NOT
  assumed anywhere; whatever the evidence shows is what you review.
*/
SET NOCOUNT ON;
DECLARE @Apply bit = 0;                                      -- 1 = insert the ReadyToPromote rows
DECLARE @VerifiedBy nvarchar(200) = N'review_crm_project_map.sql';   -- set to the operator's name when applying

;WITH ev AS
(
    SELECT CrmProjectId, ProjectCode, CompanyId, COUNT(*) AS Units, SUM(ObservedCount) AS Observations, MAX(LastObservedUtc) AS LastObservedUtc
      FROM dbo.CollectionsCrmProjectMapEvidence GROUP BY CrmProjectId, ProjectCode, CompanyId
),
pairs AS (SELECT CrmProjectId, COUNT(*) AS Pairs FROM ev GROUP BY CrmProjectId),
seen AS
(
    SELECT CrmProjectId FROM dbo.CollectionsCrmProjectMapEvidence
    UNION
    SELECT o.ProjectId FROM dbo.CollectionsCrmOwnerState s JOIN dbo.CollectionsCrmUnitOwner o ON o.RunId = s.CurrentRunId WHERE s.Id = 1 AND o.ProjectId IS NOT NULL
)
SELECT s.CrmProjectId,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.CollectionsCrmProjectMap m WHERE m.CrmProjectId = s.CrmProjectId AND m.IsActive = 1) THEN 'Mapped'
            WHEN p.Pairs = 1 THEN 'ReadyToPromote'
            WHEN p.Pairs > 1 THEN 'Ambiguous'
            ELSE 'Unresolved' END AS State,
       e.ProjectCode, e.CompanyId, e.Units, e.Observations, e.LastObservedUtc
  INTO #state
  FROM seen s
  LEFT JOIN pairs p ON p.CrmProjectId = s.CrmProjectId
  LEFT JOIN ev e ON e.CrmProjectId = s.CrmProjectId;

SELECT * FROM #state ORDER BY CASE State WHEN 'Ambiguous' THEN 0 WHEN 'Unresolved' THEN 1 WHEN 'ReadyToPromote' THEN 2 ELSE 3 END, CrmProjectId;
SELECT v.* FROM dbo.CollectionsCrmProjectMapEvidence v JOIN (SELECT DISTINCT CrmProjectId, State FROM #state) s ON s.CrmProjectId = v.CrmProjectId AND s.State IN ('ReadyToPromote', 'Ambiguous') ORDER BY v.CrmProjectId, v.ProjectCode, v.CrmUnitId;

IF @Apply = 1
BEGIN
    INSERT dbo.CollectionsCrmProjectMap (CrmProjectId, ProjectCode, CompanyId, VerifiedBy, Note)
    SELECT s.CrmProjectId, s.ProjectCode, s.CompanyId, @VerifiedBy, CONCAT(N'Promoted from ', s.Units, N' PACT-contract evidence unit(s)')
      FROM #state s
     WHERE s.State = 'ReadyToPromote'
       AND NOT EXISTS (SELECT 1 FROM dbo.CollectionsCrmProjectMap m WHERE m.CrmProjectId = s.CrmProjectId AND m.IsActive = 1);
    PRINT CONCAT(N'Promoted ', @@ROWCOUNT, N' project(s).');
END
ELSE PRINT N'Preview only (@Apply = 0): nothing was written.';
DROP TABLE #state;
