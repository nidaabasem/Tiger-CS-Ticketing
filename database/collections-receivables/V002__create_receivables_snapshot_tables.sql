/*
  V002 - Local receivables snapshot tables (TigerCsTicketing). Idempotent; creates only what is missing, never drops data.

    CollectionsReceivableRun          one row per refresh attempt (history)
    CollectionsReceivableRunCompany   one row per company per run (history; raw/published counts, errors)
    CollectionsReceivableCompanyState one row per company: pointer to the CURRENT published run + last attempt/success
    CollectionsReceivableSnapshot     published rows, partitioned by RunId; readers only see CompanyState.CurrentRunId
    CollectionsReceivableStaging      landing table for INSERT ... EXEC; column ORDER matters (see V005)

  Publishing never updates rows in place: new rows are inserted under a new RunId, then CurrentRunId is flipped in a
  short transaction, then older rows are deleted. A failed refresh therefore leaves the previous snapshot untouched.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.CollectionsReceivableRun', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableRun
(
    RunId              uniqueidentifier NOT NULL CONSTRAINT PK_CollectionsReceivableRun PRIMARY KEY,
    TriggerSource      nvarchar(50)     NOT NULL,
    RequestedCompanyId int              NULL,
    StartedUtc         datetime2(3)     NOT NULL,
    FinishedUtc        datetime2(3)     NULL,
    Status             varchar(20)      NOT NULL,  -- Running | Succeeded | PartialFailure | Failed | Abandoned
    Message            nvarchar(1000)   NULL
);

IF OBJECT_ID(N'dbo.CollectionsReceivableRunCompany', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableRunCompany
(
    RunId                     uniqueidentifier NOT NULL,
    CompanyId                 int              NOT NULL,
    StartedUtc                datetime2(3)     NOT NULL,
    FinishedUtc               datetime2(3)     NULL,
    Status                    varchar(20)      NOT NULL,  -- Running | Succeeded | Failed
    RawRows                   int              NULL,
    PublishedRows             int              NULL,
    ExcludedZeroRows          int              NULL,
    ExcludedInvalidUnitRows   int              NULL,
    ExcludedInvalidIdentityRows int            NULL,
    ShapeVariant              tinyint          NULL,
    FetchMs                   int              NULL,      -- INSERT ... EXEC from PACT (the slow, remote part)
    ValidateMs                int              NULL,      -- staging validation
    PublishMs                 int              NULL,      -- insert under the new run id + pointer flip + cleanup
    ErrorNumber               int              NULL,
    ErrorMessage              nvarchar(1000)   NULL,
    CONSTRAINT PK_CollectionsReceivableRunCompany PRIMARY KEY (RunId, CompanyId),
    CONSTRAINT FK_CollectionsReceivableRunCompany_Run FOREIGN KEY (RunId) REFERENCES dbo.CollectionsReceivableRun (RunId)
);

IF OBJECT_ID(N'dbo.CollectionsReceivableCompanyState', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableCompanyState
(
    CompanyId                    int              NOT NULL CONSTRAINT PK_CollectionsReceivableCompanyState PRIMARY KEY,
    CurrentRunId                 uniqueidentifier NULL,      -- NULL = never successfully loaded (reads must say "not loaded", not "empty")
    LastAttemptRunId             uniqueidentifier NULL,
    LastAttemptUtc               datetime2(3)     NULL,
    LastAttemptStatus            varchar(20)      NOT NULL CONSTRAINT DF_CRCS_LastAttemptStatus DEFAULT ('Never'),  -- Never | Succeeded | Failed
    LastSuccessUtc               datetime2(3)     NULL,
    LastErrorNumber              int              NULL,
    LastError                    nvarchar(1000)   NULL,
    ConsecutiveFailures          int              NOT NULL CONSTRAINT DF_CRCS_Failures DEFAULT (0),
    SnapshotRowCount             int              NOT NULL CONSTRAINT DF_CRCS_Rows DEFAULT (0),
    RawRowCount                  int              NOT NULL CONSTRAINT DF_CRCS_Raw DEFAULT (0),
    ExcludedZeroRows             int              NOT NULL CONSTRAINT DF_CRCS_Zero DEFAULT (0),
    ExcludedInvalidUnitRows      int              NOT NULL CONSTRAINT DF_CRCS_BadUnit DEFAULT (0),
    ExcludedInvalidUnitAmount    decimal(19,4)    NOT NULL CONSTRAINT DF_CRCS_BadUnitAmt DEFAULT (0),
    ExcludedInvalidIdentityRows  int              NOT NULL CONSTRAINT DF_CRCS_BadId DEFAULT (0),
    ExcludedInvalidIdentityAmount decimal(19,4)   NOT NULL CONSTRAINT DF_CRCS_BadIdAmt DEFAULT (0),
    ContradictoryStatusRows      int              NOT NULL CONSTRAINT DF_CRCS_Contra DEFAULT (0),
    UnknownStatusRows            int              NOT NULL CONSTRAINT DF_CRCS_Unknown DEFAULT (0),
    CoverageFromDate             date             NULL,      -- instalment due-date window the published snapshot covers
    CoverageThroughDate          date             NULL,
    SourceMinAmount              int              NULL,
    ShapeVariant                 tinyint          NULL,      -- result-set shape that worked last (see V005)
    StatusColumnPresent          bit              NULL,
    PaidRetained                 bit              NOT NULL CONSTRAINT DF_CRCS_PaidRetained DEFAULT (0),       -- the snapshot holds fully paid (Amount = 0) instalments
    BreakdownAvailable           bit              NOT NULL CONSTRAINT DF_CRCS_Breakdown DEFAULT (0),          -- the source returned original + paid amounts
    UnclassifiedRows             int              NOT NULL CONSTRAINT DF_CRCS_Unclassified DEFAULT (0),       -- retained rows whose payment status is Unknown
    CONSTRAINT CK_CollectionsReceivableCompanyState_Company CHECK (CompanyId IN (4, 32))
);

MERGE dbo.CollectionsReceivableCompanyState AS t
USING (VALUES (4), (32)) AS s (CompanyId) ON t.CompanyId = s.CompanyId
WHEN NOT MATCHED THEN INSERT (CompanyId) VALUES (s.CompanyId);

IF OBJECT_ID(N'dbo.CollectionsReceivableSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CollectionsReceivableSnapshot
    (
        SnapshotRowId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionsReceivableSnapshot PRIMARY KEY NONCLUSTERED,
        RunId         uniqueidentifier NOT NULL,
        CompanyId     int              NOT NULL,
        TenantId      nvarchar(100)    NOT NULL,
        FullName      nvarchar(400)    NOT NULL,
        Mobile        nvarchar(100)    NOT NULL,
        Email         nvarchar(400)    NOT NULL,
        UnitId        bigint           NOT NULL,   -- always > 0 (UnitID 0/NULL rows are excluded and counted at publish)
        UnitCode      nvarchar(200)    NOT NULL,
        ProjectCode   nvarchar(200)    NOT NULL,
        TowerNumber   nvarchar(20)     NULL,       -- derived from UnitCode by dbo.fn_CollectionsTowerNumber
        VoucherNumber nvarchar(100)    NOT NULL,
        ChequeNumber  nvarchar(100)    NOT NULL,
        DueDate       datetime         NOT NULL,
        Amount        decimal(19,4)    NOT NULL,   -- REMAINING unpaid amount of the instalment (0 = fully paid, retained only when PaidRetained)
        OriginalAmount decimal(19,4)   NULL,       -- the instalment's original amount; NULL when the source does not return it (never guessed)
        PaidAmount    decimal(19,4)    NULL,       -- amount allocated to the instalment; NULL when the source does not return it
        PaymentStatus varchar(16)      NOT NULL CONSTRAINT DF_CRS_PaymentStatus DEFAULT ('Unknown'),   -- Unpaid | PartiallyPaid | FullyPaid | Unknown
        SourceStatus  nvarchar(50)     NULL,
        LoadedUtc     datetime2(3)     NOT NULL
    );
    CREATE CLUSTERED INDEX CX_CollectionsReceivableSnapshot ON dbo.CollectionsReceivableSnapshot (CompanyId, RunId, DueDate);
    CREATE INDEX IX_CollectionsReceivableSnapshot_Tower ON dbo.CollectionsReceivableSnapshot (CompanyId, RunId, TowerNumber) INCLUDE (DueDate, Amount);
END;

-- Upgrades of a database created by an earlier version of this script (idempotent).
IF COL_LENGTH(N'dbo.CollectionsReceivableSnapshot', N'OriginalAmount') IS NULL ALTER TABLE dbo.CollectionsReceivableSnapshot ADD OriginalAmount decimal(19,4) NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableSnapshot', N'PaidAmount') IS NULL ALTER TABLE dbo.CollectionsReceivableSnapshot ADD PaidAmount decimal(19,4) NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableSnapshot', N'PaymentStatus') IS NULL ALTER TABLE dbo.CollectionsReceivableSnapshot ADD PaymentStatus varchar(16) NOT NULL CONSTRAINT DF_CRS_PaymentStatus DEFAULT ('Unknown');
IF COL_LENGTH(N'dbo.CollectionsReceivableCompanyState', N'PaidRetained') IS NULL ALTER TABLE dbo.CollectionsReceivableCompanyState ADD PaidRetained bit NOT NULL CONSTRAINT DF_CRCS_PaidRetained DEFAULT (0);
IF COL_LENGTH(N'dbo.CollectionsReceivableCompanyState', N'BreakdownAvailable') IS NULL ALTER TABLE dbo.CollectionsReceivableCompanyState ADD BreakdownAvailable bit NOT NULL CONSTRAINT DF_CRCS_Breakdown DEFAULT (0);
IF COL_LENGTH(N'dbo.CollectionsReceivableCompanyState', N'UnclassifiedRows') IS NULL ALTER TABLE dbo.CollectionsReceivableCompanyState ADD UnclassifiedRows int NOT NULL CONSTRAINT DF_CRCS_Unclassified DEFAULT (0);
IF OBJECT_ID(N'dbo.CollectionsReceivableStaging', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CollectionsReceivableStaging', N'PaymentTermAccountId') IS NULL ALTER TABLE dbo.CollectionsReceivableStaging ADD PaymentTermAccountId bigint NULL;
IF OBJECT_ID(N'dbo.CollectionsReceivableStaging', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CollectionsReceivableStaging', N'PlanAmount') IS NULL ALTER TABLE dbo.CollectionsReceivableStaging ADD PlanAmount decimal(19,4) NULL;
IF OBJECT_ID(N'dbo.CollectionsReceivableStaging', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CollectionsReceivableStaging', N'AllocatedAmount') IS NULL ALTER TABLE dbo.CollectionsReceivableStaging ADD AllocatedAmount decimal(19,4) NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableRunCompany', N'FetchMs') IS NULL ALTER TABLE dbo.CollectionsReceivableRunCompany ADD FetchMs int NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableRunCompany', N'ValidateMs') IS NULL ALTER TABLE dbo.CollectionsReceivableRunCompany ADD ValidateMs int NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableRunCompany', N'PublishMs') IS NULL ALTER TABLE dbo.CollectionsReceivableRunCompany ADD PublishMs int NULL;

-- Apartment lookup: the page procedure fetches the instalments of the (at most 100) apartments on the requested page by
-- (CompanyId, RunId, TenantId, UnitId) instead of scanning the company's rows.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsReceivableSnapshot') AND name = N'IX_CollectionsReceivableSnapshot_Apartment')
    CREATE INDEX IX_CollectionsReceivableSnapshot_Apartment ON dbo.CollectionsReceivableSnapshot (CompanyId, RunId, TenantId, UnitId) INCLUDE (DueDate, Amount);

-- Per-run tower totals, written at publish time. The "towers that are not in the tower list" report reads this tiny table
-- (a handful of rows) instead of scanning the whole snapshot on every page view.
IF OBJECT_ID(N'dbo.CollectionsReceivableTowerSummary', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableTowerSummary
(
    CompanyId   int              NOT NULL,
    RunId       uniqueidentifier NOT NULL,
    TowerNumber nvarchar(20)     NULL,
    RowCnt      int              NOT NULL,
    Amount      decimal(19,4)    NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsReceivableTowerSummary') AND name = N'CX_CollectionsReceivableTowerSummary')
    CREATE CLUSTERED INDEX CX_CollectionsReceivableTowerSummary ON dbo.CollectionsReceivableTowerSummary (CompanyId, RunId);

-- Campaign engine support (V007). The publish step (V004) prepares each run for set-based campaign reads:
--   * UnitSeq / TenantSeq / RowFlags on every row: dense integer keys (UnitSeq follows the campaign order: tenant, unit code, unit id - binary
--     collation - within the company) so the read groups and orders integers instead of strings, from a narrow covering index.
--   * CollectionsReceivableUnit / CollectionsReceivableUnitText: one row per unit (CompanyId, TenantId, UnitId, UnitCode) of the run with its static facts
--     ("contact details vary across the unit's rows", ids and validity of its phone / e-mail) and its display text.
--   * ExoticTextRows (CompanyState): NULL = the current run was NOT prepared (published before V007) or > 0 = it holds tenant / unit / name / status
--     text whose edge whitespace .NET trims but T-SQL cannot (tab, NBSP, ...). The application then evaluates campaigns itself (in memory), never silently.
IF COL_LENGTH(N'dbo.CollectionsReceivableCompanyState', N'ExoticTextRows') IS NULL ALTER TABLE dbo.CollectionsReceivableCompanyState ADD ExoticTextRows int NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableSnapshot', N'UnitSeq') IS NULL ALTER TABLE dbo.CollectionsReceivableSnapshot ADD UnitSeq int NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableSnapshot', N'TenantSeq') IS NULL ALTER TABLE dbo.CollectionsReceivableSnapshot ADD TenantSeq int NULL;
IF COL_LENGTH(N'dbo.CollectionsReceivableSnapshot', N'RowFlags') IS NULL ALTER TABLE dbo.CollectionsReceivableSnapshot ADD RowFlags tinyint NULL;   -- bit 1: SourceStatus = 'Paid'
GO
-- Narrow covering index of the campaign window scan (CompanyId, RunId, DueDate range): a fraction of the clustered row width.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsReceivableSnapshot') AND name = N'IX_CollectionsReceivableSnapshot_Campaign')
    CREATE INDEX IX_CollectionsReceivableSnapshot_Campaign ON dbo.CollectionsReceivableSnapshot (CompanyId, RunId, DueDate)
        INCLUDE (UnitSeq, TenantSeq, Amount, SnapshotRowId, RowFlags, TowerNumber);
GO

-- Normalised contact values. The application owns the phone / e-mail normalisation (E.164 rules, System.Net.Mail validation); the campaign
-- procedure joins this dictionary instead of re-implementing it in T-SQL, so both evaluation paths agree by construction. A pure function of
-- the raw value (no run id): the publish step adds every raw value with Norm NULL ("pending"), the application fills Norm / NormVersion.
IF OBJECT_ID(N'dbo.CollectionsContactNorm', N'U') IS NULL
CREATE TABLE dbo.CollectionsContactNorm
(
    ContactId   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionsContactNorm PRIMARY KEY,
    Kind        tinyint       NOT NULL,                                       -- 1 = phone, 2 = e-mail
    Raw         nvarchar(400) COLLATE Latin1_General_BIN2 NOT NULL,           -- exact source text; never empty (empty = invalid, needs no row)
    Norm        nvarchar(400) NULL,                                           -- '' = invalid; NULL = not normalised yet
    NormVersion tinyint       NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsContactNorm') AND name = N'UX_CollectionsContactNorm_Raw')
    CREATE UNIQUE INDEX UX_CollectionsContactNorm_Raw ON dbo.CollectionsContactNorm (Kind, Raw) WITH (IGNORE_DUP_KEY = ON);   -- two concurrent publishes may register the same text
GO

-- One narrow row per unit (CompanyId, TenantId, UnitId, UnitCode) of a run: the static facts the campaign read needs for EVERY unit, kept small so reading
-- 30-100 k of them costs little. PhoneOk / EmailOk say whether the unit's phone / e-mail text normalised to a valid value (NULL = not normalised yet); the
-- PhoneVer / EmailVer columns record the normalisation version they were derived with (dbo.usp_Collections_SyncUnitContacts fills them from the dictionary).
IF OBJECT_ID(N'dbo.CollectionsReceivableUnit', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableUnit
(
    CompanyId   int              NOT NULL,
    RunId       uniqueidentifier NOT NULL,
    UnitSeq     int              NOT NULL,       -- order: TenantId, UnitCode (binary), UnitId within the company
    TenantSeq   int              NOT NULL,       -- dense rank of the tenant
    CodeSeq     int              NOT NULL,       -- dense rank of (tenant, unit code)
    UnitId      bigint           NOT NULL,
    Variants    bit              NOT NULL,       -- the unit's rows do not all carry one byte-identical (name, phone, e-mail, project): resolved exactly at read time
    BadCode     bit              NOT NULL,       -- unit code blank or '0' (MissingUnitIdentity)
    PhoneId     int              NULL,           -- CollectionsContactNorm.ContactId; NULL = no phone text
    EmailId     int              NULL,
    PhoneOk     bit              NULL,           -- normalised phone is valid (0 when there is none); NULL = not normalised yet
    EmailOk     bit              NULL,
    PhoneVer    tinyint          NULL,
    EmailVer    tinyint          NULL,
    CONSTRAINT PK_CollectionsReceivableUnit PRIMARY KEY CLUSTERED (CompanyId, RunId, UnitSeq)
);
GO
-- The unit's display text (page and search only), apart from the narrow table above.
IF OBJECT_ID(N'dbo.CollectionsReceivableUnitText', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableUnitText
(
    CompanyId   int              NOT NULL,
    RunId       uniqueidentifier NOT NULL,
    UnitSeq     int              NOT NULL,
    TenantId    nvarchar(100)    COLLATE Latin1_General_BIN2 NOT NULL,
    UnitCode    nvarchar(200)    COLLATE Latin1_General_BIN2 NOT NULL,
    FullName    nvarchar(400)    NOT NULL,       -- exact for Variants = 0, a representative otherwise
    ProjectCode nvarchar(200)    NOT NULL,
    TowerNumber nvarchar(20)     NULL,
    CONSTRAINT PK_CollectionsReceivableUnitText PRIMARY KEY CLUSTERED (CompanyId, RunId, UnitSeq)
);
GO

-- Raw landing table. Column ORDER must match the PACT procedure result for INSERT ... EXEC; V005 maps four known
-- shape variants by explicit column list, so every column is nullable and generously typed. Never a reporting table.
IF OBJECT_ID(N'dbo.CollectionsReceivableStaging', N'U') IS NULL
CREATE TABLE dbo.CollectionsReceivableStaging
(
    StagingRowId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionsReceivableStaging PRIMARY KEY,
    CompanyID     int            NULL,
    ProjectCode   nvarchar(200)  NULL,
    UnitCode      nvarchar(200)  NULL,
    TenantID      nvarchar(100)  NULL,
    FullName      nvarchar(400)  NULL,
    Mobile        nvarchar(100)  NULL,
    Email         nvarchar(400)  NULL,
    UnitID        bigint         NULL,
    VoucherNumber nvarchar(100)  NULL,
    ChequeNumber  nvarchar(100)  NULL,
    DueDate       datetime       NULL,
    Amount        decimal(19,4)  NULL,
    Status        nvarchar(50)   NULL,
    PaymentTermAccountId bigint  NULL,      -- companion (V2) shape only
    PlanAmount    decimal(19,4)  NULL,      -- companion (V2) shape only: the instalment's original amount
    AllocatedAmount decimal(19,4) NULL      -- companion (V2) shape only: the amount allocated (paid) to the instalment
);
PRINT N'V002 complete.';
