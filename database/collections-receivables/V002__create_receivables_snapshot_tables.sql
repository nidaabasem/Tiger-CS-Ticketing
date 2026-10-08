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
        Amount        decimal(19,4)    NOT NULL,   -- remaining unpaid amount of the instalment; always > 0
        SourceStatus  nvarchar(50)     NULL,
        LoadedUtc     datetime2(3)     NOT NULL
    );
    CREATE CLUSTERED INDEX CX_CollectionsReceivableSnapshot ON dbo.CollectionsReceivableSnapshot (CompanyId, RunId, DueDate);
    CREATE INDEX IX_CollectionsReceivableSnapshot_Tower ON dbo.CollectionsReceivableSnapshot (CompanyId, RunId, TowerNumber) INCLUDE (DueDate, Amount);
END;

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
    Status        nvarchar(50)   NULL
);
PRINT N'V002 complete.';
