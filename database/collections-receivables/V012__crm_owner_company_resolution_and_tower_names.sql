/*
  V012 - CRM owners: tie each owner to ONE company by the unit itself, keep the CRM project name, and resolve tower names from their source (TigerCsTicketing).

  Why (traced in the code, see docs/Collections/Customer-Unit-Linking.md "CRM contact and tower names"):
    * V010 gave a CRM owner a company ONLY through dbo.CollectionsTowers and only when the tower number belongs to exactly ONE company. A tower missing from that table (TP119 is "intentionally not seeded"
      by V001) or present under both companies left the owner with CompanyId NULL, and a row without a company is never joined to a unit: the CRM name / mobile / e-mail of such a unit never reached
      Receivables, Campaigns or Payment, and the page simply showed the (possibly empty) PACT contact.
    * Tower names came ONLY from dbo.CollectionsTowers, so a tower without a usable row there showed its bare number ("119").

  What this script does (idempotent, no data is invented, nothing is deleted):
    1. dbo.CollectionsCrmUnitOwner gains ProjectName (what CRM calls the tower), TowerNumber (stored once, so reads need no scalar function) and CompanySource
       ('Map' = the verified dbo.CollectionsCrmProjectMap names the company, 'Tower' = dbo.CollectionsTowers has the tower in exactly one company,
        'Snapshot' = exactly ONE company's PACT snapshot holds this exact unit code, 'Ambiguous' = BOTH companies hold it, NULL = no company holds it).
       dbo.CollectionsCrmOwnerState gains AmbiguousRows and LastNote (why rows were not stored).
    2. usp_Collections_PublishCrmUnitOwners: a verified manual project map (one PACT code for the CRM project) now wins over the code the feed sent; the company is then decided as above.
       A unit key held by both companies is NEVER tied to either (CompanyAmbiguous -> the unit is flagged "needs review").
    3. usp_Collections_ResolveCrmOwnerCompanies: re-runs step "Snapshot" against the current PACT snapshot (the application calls it after every receivables refresh).
    4. fn_Collections_CrmUnitLinks / usp_Collections_GetCrmUnitLinks / usp_Collections_GetCrmOwnerState expose CompanyAmbiguous, AmbiguousRows and LastNote.
    5. dbo.fn_Collections_TowerNames(): the one source of tower names: dbo.CollectionsTowers when its name is a real name (not blank and not just the number or TP<number>), else the single
       project name CRM holds for that company's tower. Two different CRM names for one tower = no name (nothing is chosen).
    6. Backfills the new columns of the CURRENT CRM run and resolves its companies, so the fix is visible without waiting for the next CRM refresh.

  Order: V001, V003, V010, V011, R001, then THIS script, then R002, R003, R004 (R004 reads fn_Collections_TowerNames).  Needs V010, V011 and R001.
*/
SET NOCOUNT ON;
GO

IF COL_LENGTH(N'dbo.CollectionsCrmUnitOwner', N'ProjectName') IS NULL
    ALTER TABLE dbo.CollectionsCrmUnitOwner ADD ProjectName nvarchar(400) NOT NULL CONSTRAINT DF_CollectionsCrmUnitOwner_ProjectName DEFAULT (N'');
IF COL_LENGTH(N'dbo.CollectionsCrmUnitOwner', N'TowerNumber') IS NULL
    ALTER TABLE dbo.CollectionsCrmUnitOwner ADD TowerNumber nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.CollectionsCrmUnitOwner', N'CompanySource') IS NULL
    ALTER TABLE dbo.CollectionsCrmUnitOwner ADD CompanySource varchar(10) NULL;
IF COL_LENGTH(N'dbo.CollectionsCrmOwnerState', N'AmbiguousRows') IS NULL
    ALTER TABLE dbo.CollectionsCrmOwnerState ADD AmbiguousRows int NOT NULL CONSTRAINT DF_CollectionsCrmOwnerState_Ambiguous DEFAULT (0);
IF COL_LENGTH(N'dbo.CollectionsCrmOwnerState', N'LastNote') IS NULL
    ALTER TABLE dbo.CollectionsCrmOwnerState ADD LastNote nvarchar(1000) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsCrmUnitOwner') AND name = N'IX_CollectionsCrmUnitOwner_UnitKey')
    CREATE INDEX IX_CollectionsCrmUnitOwner_UnitKey ON dbo.CollectionsCrmUnitOwner (RunId, UnitKey) INCLUDE (CompanyId, CompanySource);
GO

-- 1. Store: the project name travels with the owner.
CREATE OR ALTER PROCEDURE dbo.usp_Collections_StoreCrmUnitOwners @RunId uniqueidentifier, @Json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.CollectionsCrmUnitOwner (RunId, UnitKey, ProjectCode, UnitNumber, LeadId, LeadStatus, CrmCustomerId, CrmUnitId, ProjectId, FullName, Mobile, Email, PhoneNorm, EmailNorm, ProjectName)
    SELECT @RunId, j.UnitKey, j.ProjectCode, j.UnitNumber, j.LeadId, j.LeadStatus, j.CustomerId, j.UnitId, j.ProjectId, ISNULL(j.FullName, N''), ISNULL(j.Mobile, N''), ISNULL(j.Email, N''),
           ISNULL(j.PhoneNorm, N''), ISNULL(j.EmailNorm, N''), ISNULL(LTRIM(RTRIM(j.ProjectName)), N'')
      FROM OPENJSON(@Json) WITH (UnitKey nvarchar(220) '$.unitKey', ProjectCode nvarchar(50) '$.projectCode', UnitNumber nvarchar(100) '$.unitNumber', LeadId int '$.leadId',
                                 LeadStatus int '$.leadStatus', CustomerId int '$.customerId', UnitId int '$.unitId', ProjectId int '$.projectId', FullName nvarchar(400) '$.fullName',
                                 Mobile nvarchar(100) '$.mobile', Email nvarchar(320) '$.email', PhoneNorm nvarchar(50) '$.phoneNorm', EmailNorm nvarchar(320) '$.emailNorm',
                                 ProjectName nvarchar(400) '$.projectName') j;
END;
GO

-- 2. The company of an owner, decided by the unit itself. Core (no result set): used by the publish and by the standalone procedure below.
--    'Map' and 'Tower' ties are decisions made elsewhere and are kept; 'Snapshot' / 'Ambiguous' are recomputed from the CURRENT PACT snapshot every time.
CREATE OR ALTER PROCEDURE dbo.usp_Collections_ResolveCrmOwnerCompaniesCore @RunId uniqueidentifier, @Resolved int OUTPUT, @Ambiguous int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @Resolved = 0; SET @Ambiguous = 0;

    UPDATE dbo.CollectionsCrmUnitOwner SET CompanyId = NULL, CompanySource = NULL WHERE RunId = @RunId AND CompanySource IN ('Snapshot', 'Ambiguous');

    -- The units the current PACT snapshot holds, per company (the exact unit code, never the tower number alone).
    CREATE TABLE #held (CompanyId int NOT NULL, UnitKey nvarchar(220) COLLATE Latin1_General_BIN2 NOT NULL, PRIMARY KEY (CompanyId, UnitKey));
    SELECT DISTINCT s.CompanyId, s.UnitCode
      INTO #codes
      FROM dbo.CollectionsRcvCompany c
      JOIN dbo.CollectionsRcvLine s ON s.CompanyId = c.CompanyId AND s.RunId = c.CurrentRunId
     WHERE c.CurrentRunId IS NOT NULL AND s.UnitId > 0 AND s.UnitCode NOT LIKE N'%*%' AND LTRIM(RTRIM(s.UnitCode)) NOT IN (N'', N'0');
    INSERT #held (CompanyId, UnitKey)
    SELECT DISTINCT CompanyId, dbo.fn_CollectionsUnitKey(UnitCode) FROM #codes WHERE dbo.fn_CollectionsUnitKey(UnitCode) IS NOT NULL;

    UPDATE o SET CompanyId = h.CompanyId, CompanySource = 'Snapshot'
      FROM dbo.CollectionsCrmUnitOwner o
      JOIN (SELECT UnitKey, MIN(CompanyId) AS CompanyId FROM #held GROUP BY UnitKey HAVING COUNT(*) = 1) h ON h.UnitKey = o.UnitKey
     WHERE o.RunId = @RunId AND o.CompanyId IS NULL;
    SET @Resolved = @@ROWCOUNT;

    UPDATE o SET CompanySource = 'Ambiguous'
      FROM dbo.CollectionsCrmUnitOwner o
      JOIN (SELECT UnitKey FROM #held GROUP BY UnitKey HAVING COUNT(*) > 1) h ON h.UnitKey = o.UnitKey
     WHERE o.RunId = @RunId AND o.CompanyId IS NULL;
    SET @Ambiguous = @@ROWCOUNT;
END;
GO

-- Standalone: ties the CURRENT run's owners again against the current snapshot (the application calls it after every receivables refresh). Returns (Resolved, Ambiguous).
CREATE OR ALTER PROCEDURE dbo.usp_Collections_ResolveCrmOwnerCompanies
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @run uniqueidentifier = (SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1), @resolved int = 0, @ambiguous int = 0;
    IF @run IS NOT NULL
    BEGIN
        EXEC dbo.usp_Collections_ResolveCrmOwnerCompaniesCore @RunId = @run, @Resolved = @resolved OUTPUT, @Ambiguous = @ambiguous OUTPUT;
        UPDATE dbo.CollectionsCrmOwnerState
           SET UnlinkedRows = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @run AND CompanyId IS NULL),
               AmbiguousRows = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @run AND CompanySource = 'Ambiguous')
         WHERE Id = 1 AND CurrentRunId = @run;
    END;
    SELECT @resolved AS Resolved, @ambiguous AS Ambiguous;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_PublishCrmUnitOwners @RunId uniqueidentifier, @ExpectedRows int, @Note nvarchar(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @n int = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId);
    IF @n = 0 OR @n <> @ExpectedRows
    BEGIN
        DELETE FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId;
        THROW 50040, N'The staged CRM owners do not match the expected row count; nothing was published.', 1;
    END;

    -- a. A verified manual project map (exactly ONE PACT code for the CRM project) wins over the code the feed sent; it may also name the company.
    UPDATE o SET ProjectCode = m.ProjectCode, UnitKey = dbo.fn_CollectionsUnitKey(m.ProjectCode + N'-' + o.UnitNumber),
                 CompanyId = m.CompanyId, CompanySource = CASE WHEN m.CompanyId IS NOT NULL THEN 'Map' END
      FROM dbo.CollectionsCrmUnitOwner o
      JOIN (SELECT CrmProjectId, MIN(LTRIM(RTRIM(ProjectCode))) AS ProjectCode,
                   CASE WHEN COUNT(DISTINCT CompanyId) = 1 AND COUNT(CompanyId) = COUNT(*) THEN MIN(CompanyId) END AS CompanyId
              FROM dbo.CollectionsCrmProjectMap WHERE IsActive = 1
             GROUP BY CrmProjectId HAVING COUNT(DISTINCT UPPER(LTRIM(RTRIM(ProjectCode)))) = 1) m ON m.CrmProjectId = o.ProjectId
     WHERE o.RunId = @RunId AND dbo.fn_CollectionsUnitKey(m.ProjectCode + N'-' + o.UnitNumber) IS NOT NULL;

    UPDATE dbo.CollectionsCrmUnitOwner SET TowerNumber = dbo.fn_CollectionsTowerNumber(UnitKey) WHERE RunId = @RunId;

    -- b. One company per tower: a tower number that belongs to exactly one company in dbo.CollectionsTowers ties its owners to it.
    UPDATE o SET CompanyId = t.CompanyId, CompanySource = 'Tower'
      FROM dbo.CollectionsCrmUnitOwner o
      JOIN (SELECT LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) AS TowerNumber, MIN(CompanyId) AS CompanyId
              FROM dbo.CollectionsTowers GROUP BY LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) HAVING COUNT(DISTINCT CompanyId) = 1) t
        ON t.TowerNumber = o.TowerNumber
     WHERE o.RunId = @RunId AND o.CompanyId IS NULL;

    -- c. Every other owner (the tower is in both companies, or in none): the company whose PACT snapshot holds this exact unit; both = ambiguous, nobody chosen.
    DECLARE @resolved int, @ambiguous int;
    EXEC dbo.usp_Collections_ResolveCrmOwnerCompaniesCore @RunId = @RunId, @Resolved = @resolved OUTPUT, @Ambiguous = @ambiguous OUTPUT;
    DECLARE @unlinked int = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId AND CompanyId IS NULL);
    DECLARE @amb int = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId AND CompanySource = 'Ambiguous');

    DECLARE @old uniqueidentifier = (SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1), @now datetime2(3) = SYSUTCDATETIME();
    BEGIN TRANSACTION;
        UPDATE dbo.CollectionsCrmOwnerState
           SET CurrentRunId = @RunId, LastSuccessUtc = @now, LastAttemptUtc = @now, LastAttemptStatus = 'Succeeded', LastError = NULL, RowCount_ = @n, UnlinkedRows = @unlinked,
               AmbiguousRows = @amb, LastNote = LEFT(@Note, 1000)
         WHERE Id = 1;
    COMMIT;
    DELETE FROM dbo.CollectionsCrmUnitOwner WHERE RunId <> @RunId;
    SELECT @n - @unlinked AS LinkedRows;
END;
GO

-- 3. Reads.
CREATE OR ALTER FUNCTION dbo.fn_Collections_CrmUnitLinks ()
RETURNS TABLE
AS RETURN
(
    SELECT x.CompanyId, o.UnitKey,
           COUNT(DISTINCT o.CrmCustomerId) AS Customers,
           MIN(o.CrmCustomerId) AS CustomerId, MIN(o.CrmUnitId) AS CrmUnitId, MIN(o.LeadId) AS LeadId,
           ISNULL(MIN(NULLIF(o.FullName, N'')), N'') AS FullName,
           ISNULL(MIN(NULLIF(o.PhoneNorm, N'')), N'') AS PhoneNorm,
           ISNULL(MIN(NULLIF(o.EmailNorm, N'')), N'') AS EmailNorm,
           -- 1 when the CRM sale is tied to NO company because both companies hold this exact unit code: the unit is shown for review, the CRM contact is not used.
           CAST(MAX(CASE WHEN o.CompanyId IS NULL THEN 1 ELSE 0 END) AS bit) AS CompanyAmbiguous
      FROM dbo.CollectionsCrmOwnerState s
      JOIN dbo.CollectionsCrmUnitOwner o ON o.RunId = s.CurrentRunId
      CROSS APPLY (SELECT o.CompanyId AS CompanyId WHERE o.CompanyId IS NOT NULL
                   UNION ALL
                   SELECT v.c FROM (VALUES (4), (32)) v(c) WHERE o.CompanyId IS NULL AND o.CompanySource = 'Ambiguous') x
     WHERE s.Id = 1
     GROUP BY x.CompanyId, o.UnitKey
);
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCrmUnitLinks @Json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT l.CompanyId, l.UnitKey, l.Customers, l.CustomerId, l.CrmUnitId, l.LeadId, l.FullName, l.PhoneNorm, l.EmailNorm, l.CompanyAmbiguous
      FROM OPENJSON(@Json) WITH (CompanyId int '$.c', UnitKey nvarchar(220) '$.k') k
      JOIN dbo.fn_Collections_CrmUnitLinks() l ON l.CompanyId = k.CompanyId AND l.UnitKey = k.UnitKey COLLATE Latin1_General_BIN2;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCrmOwnerState
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(CASE WHEN CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS Loaded, LastSuccessUtc, LastAttemptUtc, LastAttemptStatus, LastError, RowCount_ AS TotalRows, UnlinkedRows,
           AmbiguousRows, LastNote
      FROM dbo.CollectionsCrmOwnerState WHERE Id = 1;
END;
GO

-- 4. Tower names: ONE source for every screen and export. Never a name typed into the application.
--    CollectionsTowers first (when its name is a real name); otherwise the single project name CRM holds for that company's tower.
CREATE OR ALTER FUNCTION dbo.fn_Collections_TowerNames ()
RETURNS TABLE
AS RETURN
(
    WITH own AS
    (
        SELECT t.CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) AS TowerNumber, LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName))) AS TowerName,
               ROW_NUMBER() OVER (PARTITION BY t.CompanyId, LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber)))
                                  ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) AS Rn
          FROM dbo.CollectionsTowers t
         WHERE LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName))) <> N''
           AND UPPER(LTRIM(RTRIM(CONVERT(nvarchar(400), t.TowerName)))) NOT IN
               (UPPER(LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber)))), N'TP' + UPPER(LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber)))),
                N'TP-' + UPPER(LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber)))))
    ),
    crm AS
    (
        SELECT o.CompanyId, o.TowerNumber, MIN(o.ProjectName) AS TowerName
          FROM dbo.CollectionsCrmOwnerState s
          JOIN dbo.CollectionsCrmUnitOwner o ON o.RunId = s.CurrentRunId
         WHERE s.Id = 1 AND o.CompanyId IS NOT NULL AND o.TowerNumber IS NOT NULL AND o.ProjectName <> N''
           AND UPPER(o.ProjectName) NOT IN (UPPER(o.TowerNumber), N'TP' + UPPER(o.TowerNumber), N'TP-' + UPPER(o.TowerNumber))
         GROUP BY o.CompanyId, o.TowerNumber
        HAVING COUNT(DISTINCT o.ProjectName) = 1      -- two different CRM names for one tower: nothing is chosen
    )
    SELECT w.CompanyId, w.TowerNumber, w.TowerName, CAST('CollectionsTowers' AS varchar(20)) AS NameSource FROM own w WHERE w.Rn = 1
    UNION ALL
    SELECT c.CompanyId, c.TowerNumber, c.TowerName, CAST('Crm' AS varchar(20))
      FROM crm c
     WHERE NOT EXISTS (SELECT 1 FROM own w WHERE w.Rn = 1 AND w.CompanyId = c.CompanyId AND w.TowerNumber = c.TowerNumber)
);
GO

-- 5. Backfill of the CURRENT run (a run stored before this script has no TowerNumber / CompanySource): visible at once, without waiting for the next CRM refresh.
DECLARE @run uniqueidentifier = (SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1);
IF @run IS NOT NULL
BEGIN
    UPDATE dbo.CollectionsCrmUnitOwner SET TowerNumber = dbo.fn_CollectionsTowerNumber(UnitKey) WHERE RunId = @run AND TowerNumber IS NULL;
    UPDATE dbo.CollectionsCrmUnitOwner SET CompanySource = 'Tower' WHERE RunId = @run AND CompanyId IS NOT NULL AND CompanySource IS NULL;
    IF OBJECT_ID(N'dbo.CollectionsRcvLine', N'U') IS NOT NULL
    BEGIN
        EXEC dbo.usp_Collections_ResolveCrmOwnerCompanies;
    END;
END;
PRINT N'V012 complete.';
GO
