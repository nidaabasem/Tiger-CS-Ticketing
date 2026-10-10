/*
  V010 - CRM unit owners: the bulk, local copy of "who holds an eligible sale of each unit" (Sold / Contract, not cancelled), and the unit-level link reads.

  The application loads the CRM feed (GET /TicketingSystem/GetUnitOwners, see docs/Collections/CRM-GetUnitOwners-Contract.md) in the background, normalises phone / e-mail
  with its single normaliser and stores a complete run with usp_Collections_StoreCrmUnitOwners; usp_Collections_PublishCrmUnitOwners then ties each row to ONE company
  (through dbo.CollectionsTowers - a tower number that belongs to both companies stays unlinked) and flips the pointer. A failed or empty load never replaces the data.

  Objects
    dbo.fn_CollectionsUnitKey                function   'TP140-101' / '140-101' / ' tp140-101 ' -> 'TP140-101' (mirrors CollectionsUnitKey.Normalize)
    dbo.CollectionsCrmOwnerState             table      one row: current run, last attempt / success, counts
    dbo.CollectionsCrmUnitOwner              table      one row per eligible CRM sale relation of a run (UnitKey is the link, never a name or a phone)
    dbo.fn_Collections_CrmUnitLinks          inline TVF per (company, unit key) of the CURRENT run: distinct customers (1 = Single, > 1 = Ambiguous) and, for one customer, its contact
    dbo.usp_Collections_StoreCrmUnitOwners / usp_Collections_PublishCrmUnitOwners / usp_Collections_RecordCrmOwnerFailure
    dbo.usp_Collections_GetCrmUnitLinks      bulk link read for a JSON list of (company, unit key)
    dbo.usp_Collections_GetUnitReceivables   Payment Summary: one unit's PACT identities and unpaid instalments due today or earlier + its CRM link, by unit key
  Idempotent (CREATE OR ALTER / IF NOT EXISTS). Needs V002, V003, V006.
*/
CREATE OR ALTER FUNCTION dbo.fn_CollectionsUnitKey (@UnitCode nvarchar(200))
RETURNS nvarchar(220)
AS
BEGIN
    DECLARE @s nvarchar(200) = LTRIM(RTRIM(@UnitCode));
    IF @s IS NULL OR @s = N'' RETURN NULL;
    IF UPPER(LEFT(@s, 2)) = N'TP' SET @s = LTRIM(SUBSTRING(@s, 3, 200));
    IF @s = N'' RETURN NULL;
    RETURN N'TP' + UPPER(@s);
END;
GO

IF OBJECT_ID(N'dbo.CollectionsCrmOwnerState', N'U') IS NULL
CREATE TABLE dbo.CollectionsCrmOwnerState
(
    Id               tinyint        NOT NULL CONSTRAINT PK_CollectionsCrmOwnerState PRIMARY KEY CONSTRAINT CK_CollectionsCrmOwnerState_Id CHECK (Id = 1),
    CurrentRunId     uniqueidentifier NULL,
    LastSuccessUtc   datetime2(3)   NULL,
    LastAttemptUtc   datetime2(3)   NULL,
    LastAttemptStatus varchar(20)   NULL,
    LastError        nvarchar(1000) NULL,
    RowCount_        int            NOT NULL CONSTRAINT DF_CollectionsCrmOwnerState_Rows DEFAULT (0),
    UnlinkedRows     int            NOT NULL CONSTRAINT DF_CollectionsCrmOwnerState_Unlinked DEFAULT (0)
);
GO
IF NOT EXISTS (SELECT 1 FROM dbo.CollectionsCrmOwnerState WHERE Id = 1) INSERT dbo.CollectionsCrmOwnerState (Id) VALUES (1);
GO
IF OBJECT_ID(N'dbo.CollectionsCrmUnitOwner', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CollectionsCrmUnitOwner
    (
        RunId         uniqueidentifier NOT NULL,
        OwnerSeq      int IDENTITY(1,1) NOT NULL,
        UnitKey       nvarchar(220) COLLATE Latin1_General_BIN2 NOT NULL,
        CompanyId     int            NULL,          -- set at publish from dbo.CollectionsTowers; NULL = the tower belongs to no / both companies (never linked)
        ProjectCode   nvarchar(50)   NOT NULL,
        UnitNumber    nvarchar(100)  NOT NULL,
        LeadId        int            NOT NULL,
        LeadStatus    int            NOT NULL,
        CrmCustomerId int            NOT NULL,
        CrmUnitId     int            NOT NULL,
        ProjectId     int            NOT NULL,
        FullName      nvarchar(400)  NOT NULL,
        Mobile        nvarchar(100)  NOT NULL,
        Email         nvarchar(320)  NOT NULL,
        PhoneNorm     nvarchar(50)   NOT NULL,      -- the application's normalisation ('' = missing or invalid)
        EmailNorm     nvarchar(320)  NOT NULL,
        CONSTRAINT PK_CollectionsCrmUnitOwner PRIMARY KEY CLUSTERED (RunId, OwnerSeq)
    );
    CREATE INDEX IX_CollectionsCrmUnitOwner_Unit ON dbo.CollectionsCrmUnitOwner (RunId, CompanyId, UnitKey) INCLUDE (CrmCustomerId);
END;
GO

CREATE OR ALTER FUNCTION dbo.fn_Collections_CrmUnitLinks ()
RETURNS TABLE
AS RETURN
(
    SELECT o.CompanyId, o.UnitKey,
           COUNT(DISTINCT o.CrmCustomerId) AS Customers,
           MIN(o.CrmCustomerId) AS CustomerId, MIN(o.CrmUnitId) AS CrmUnitId, MIN(o.LeadId) AS LeadId,
           ISNULL(MIN(NULLIF(o.FullName, N'')), N'') AS FullName,
           ISNULL(MIN(NULLIF(o.PhoneNorm, N'')), N'') AS PhoneNorm,
           ISNULL(MIN(NULLIF(o.EmailNorm, N'')), N'') AS EmailNorm
      FROM dbo.CollectionsCrmOwnerState s
      JOIN dbo.CollectionsCrmUnitOwner o ON o.RunId = s.CurrentRunId
     WHERE s.Id = 1 AND o.CompanyId IS NOT NULL
     GROUP BY o.CompanyId, o.UnitKey
);
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_StoreCrmUnitOwners @RunId uniqueidentifier, @Json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.CollectionsCrmUnitOwner (RunId, UnitKey, ProjectCode, UnitNumber, LeadId, LeadStatus, CrmCustomerId, CrmUnitId, ProjectId, FullName, Mobile, Email, PhoneNorm, EmailNorm)
    SELECT @RunId, j.UnitKey, j.ProjectCode, j.UnitNumber, j.LeadId, j.LeadStatus, j.CustomerId, j.UnitId, j.ProjectId, ISNULL(j.FullName, N''), ISNULL(j.Mobile, N''), ISNULL(j.Email, N''),
           ISNULL(j.PhoneNorm, N''), ISNULL(j.EmailNorm, N'')
      FROM OPENJSON(@Json) WITH (UnitKey nvarchar(220) '$.unitKey', ProjectCode nvarchar(50) '$.projectCode', UnitNumber nvarchar(100) '$.unitNumber', LeadId int '$.leadId',
                                 LeadStatus int '$.leadStatus', CustomerId int '$.customerId', UnitId int '$.unitId', ProjectId int '$.projectId', FullName nvarchar(400) '$.fullName',
                                 Mobile nvarchar(100) '$.mobile', Email nvarchar(320) '$.email', PhoneNorm nvarchar(50) '$.phoneNorm', EmailNorm nvarchar(320) '$.emailNorm') j;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_PublishCrmUnitOwners @RunId uniqueidentifier, @ExpectedRows int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @n int = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId);
    IF @n = 0 OR @n <> @ExpectedRows
    BEGIN
        DELETE FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId;
        THROW 50040, N'The staged CRM owners do not match the expected row count; nothing was published.', 1;
    END;

    -- One company per tower: a tower number that belongs to exactly one company ties its owners to it; any other tower stays unlinked (never guessed).
    UPDATE o SET CompanyId = t.CompanyId
      FROM dbo.CollectionsCrmUnitOwner o
      JOIN (SELECT LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) AS TowerNumber, MIN(CompanyId) AS CompanyId
              FROM dbo.CollectionsTowers GROUP BY LTRIM(RTRIM(CONVERT(nvarchar(20), TowerNumber))) HAVING COUNT(DISTINCT CompanyId) = 1) t
        ON t.TowerNumber = dbo.fn_CollectionsTowerNumber(o.UnitKey)
     WHERE o.RunId = @RunId;
    DECLARE @unlinked int = (SELECT COUNT(*) FROM dbo.CollectionsCrmUnitOwner WHERE RunId = @RunId AND CompanyId IS NULL);

    DECLARE @old uniqueidentifier = (SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1), @now datetime2(3) = SYSUTCDATETIME();
    BEGIN TRANSACTION;
        UPDATE dbo.CollectionsCrmOwnerState
           SET CurrentRunId = @RunId, LastSuccessUtc = @now, LastAttemptUtc = @now, LastAttemptStatus = 'Succeeded', LastError = NULL, RowCount_ = @n, UnlinkedRows = @unlinked
         WHERE Id = 1;
    COMMIT;
    DELETE FROM dbo.CollectionsCrmUnitOwner WHERE RunId <> @RunId;
    SELECT @n - @unlinked AS LinkedRows;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_RecordCrmOwnerFailure @Message nvarchar(1000)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.CollectionsCrmOwnerState SET LastAttemptUtc = SYSUTCDATETIME(), LastAttemptStatus = 'Failed', LastError = LEFT(@Message, 1000) WHERE Id = 1;
    DELETE FROM dbo.CollectionsCrmUnitOwner WHERE RunId <> ISNULL((SELECT CurrentRunId FROM dbo.CollectionsCrmOwnerState WHERE Id = 1), '00000000-0000-0000-0000-000000000000');
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCrmOwnerState
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(CASE WHEN CurrentRunId IS NULL THEN 0 ELSE 1 END AS bit) AS Loaded, LastSuccessUtc, LastAttemptUtc, LastAttemptStatus, LastError, RowCount_ AS TotalRows, UnlinkedRows
      FROM dbo.CollectionsCrmOwnerState WHERE Id = 1;
END;
GO

-- Bulk link read: @Json = [{"c":4,"k":"TP140-101"}, ...]. One row per (company, unit key) the CURRENT CRM run holds; a unit CRM does not hold is simply absent.
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetCrmUnitLinks @Json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT l.CompanyId, l.UnitKey, l.Customers, l.CustomerId, l.CrmUnitId, l.LeadId, l.FullName, l.PhoneNorm, l.EmailNorm
      FROM OPENJSON(@Json) WITH (CompanyId int '$.c', UnitKey nvarchar(220) '$.k') k
      JOIN dbo.fn_Collections_CrmUnitLinks() l ON l.CompanyId = k.CompanyId AND l.UnitKey = k.UnitKey COLLATE Latin1_General_BIN2;
END;
GO

/*
  Payment Summary by unit. @UnitKey is the normalised key (TP140-101); @CompanyId narrows to one company (NULL = every company that holds the unit).
    1 coverage        usp_Collections_GetCoverage (freshness of the PACT snapshot)
    2 identities      per (company, tenant, unit): the PACT customer of the unit (name / mobile / e-mail as PACT holds them - mobile may be empty), tower, and how many rows exist
    3 instalments     the unit's unpaid instalments due on or before @AsOfDate (Overdue < it, Due = it), oldest first, with the breakdown PACT returned
    4 crm             the CRM link of the unit (usp_Collections_GetCrmUnitLinks shape), all companies
  Rows with UnitID <= 0 or a cancelled ('*') / blank / 0 code never match.
*/
CREATE OR ALTER PROCEDURE dbo.usp_Collections_GetUnitReceivables @UnitKey nvarchar(220), @CompanyId int = NULL, @AsOfDate date
AS
BEGIN
    SET NOCOUNT ON;
    IF @CompanyId IS NOT NULL AND @CompanyId NOT IN (4, 32) THROW 50031, N'Company must be 4, 32 or NULL.', 1;
    DECLARE @Bare nvarchar(220) = SUBSTRING(@UnitKey, 3, 220), @Tomorrow datetime = CAST(DATEADD(DAY, 1, @AsOfDate) AS datetime);
    EXEC dbo.usp_Collections_GetCoverage @CompanyId;

    SELECT u.CompanyId, u.UnitSeq, u.TenantId COLLATE DATABASE_DEFAULT AS TenantId, u.UnitCode COLLATE DATABASE_DEFAULT AS UnitCode, r.UnitId, r.FullName, r.Mobile, r.Email, r.TowerNumber,
           CONVERT(nvarchar(400), tw.TowerName) AS TowerName, a.AllRows, a.OpenRows
      INTO #id
      FROM dbo.CollectionsReceivableCompanyState st
      JOIN dbo.CollectionsReceivableUnitText u ON u.CompanyId = st.CompanyId AND u.RunId = st.CurrentRunId
      CROSS APPLY (SELECT TOP (1) s.* FROM dbo.CollectionsReceivableSnapshot s
                    WHERE s.CompanyId = u.CompanyId AND s.RunId = u.RunId AND s.UnitSeq = u.UnitSeq ORDER BY s.DueDate, s.SnapshotRowId) r
      CROSS APPLY (SELECT COUNT(*) AS AllRows, SUM(CASE WHEN s.Amount > 0 THEN 1 ELSE 0 END) AS OpenRows FROM dbo.CollectionsReceivableSnapshot s
                    WHERE s.CompanyId = u.CompanyId AND s.RunId = u.RunId AND s.UnitSeq = u.UnitSeq) a
      OUTER APPLY (SELECT TOP (1) t.TowerName FROM dbo.CollectionsTowers t WHERE t.CompanyId = r.CompanyId AND LTRIM(RTRIM(CONVERT(nvarchar(20), t.TowerNumber))) = r.TowerNumber
                    ORDER BY CASE WHEN t.IsActive = 1 THEN 0 ELSE 1 END, t.TowerId) tw
     WHERE (@CompanyId IS NULL OR st.CompanyId = @CompanyId) AND st.CurrentRunId IS NOT NULL
       AND UPPER(LTRIM(RTRIM(u.UnitCode))) COLLATE Latin1_General_BIN2 IN (@UnitKey COLLATE Latin1_General_BIN2, @Bare COLLATE Latin1_General_BIN2)
       AND r.UnitId > 0 AND u.UnitCode NOT LIKE N'%*%' AND LTRIM(RTRIM(u.UnitCode)) NOT IN (N'', N'0')
    OPTION (RECOMPILE);
    SELECT CompanyId, UnitSeq, TenantId, UnitCode, UnitId, FullName, Mobile, Email, TowerNumber, TowerName, AllRows, OpenRows FROM #id ORDER BY CompanyId, TenantId, UnitSeq;

    SELECT i.CompanyId, i.TenantId, i.UnitId, s.VoucherNumber, s.ChequeNumber, s.DueDate, s.Amount AS RemainingAmount, s.OriginalAmount, s.PaidAmount, s.PaymentStatus, s.SourceStatus
      FROM #id i
      JOIN dbo.CollectionsReceivableCompanyState st ON st.CompanyId = i.CompanyId
      JOIN dbo.CollectionsReceivableSnapshot s ON s.CompanyId = i.CompanyId AND s.RunId = st.CurrentRunId AND s.UnitSeq = i.UnitSeq
     WHERE s.Amount > 0 AND s.DueDate < @Tomorrow
     ORDER BY i.CompanyId, i.UnitSeq, s.DueDate, s.VoucherNumber COLLATE Latin1_General_BIN2, s.SnapshotRowId;

    SELECT l.CompanyId, l.UnitKey, l.Customers, l.CustomerId, l.CrmUnitId, l.LeadId, l.FullName, l.PhoneNorm, l.EmailNorm
      FROM dbo.fn_Collections_CrmUnitLinks() l
     WHERE l.UnitKey = @UnitKey COLLATE Latin1_General_BIN2 AND (@CompanyId IS NULL OR l.CompanyId = @CompanyId);
END;
GO
