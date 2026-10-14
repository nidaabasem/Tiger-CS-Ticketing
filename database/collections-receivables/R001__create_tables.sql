/*
  R001 - Receivables snapshot, rebuilt (TigerCsTicketing). Idempotent: creates only what is missing, never drops or alters existing data.

  This is a NEW, self-contained pipeline  PACT -> dbo.CollectionsRcvLine -> pages.  It shares NO table, procedure, run history or lock with the previous
  snapshot (dbo.CollectionsReceivable*); that one is retired by the migration scripts in this folder.  CRM unit owners (V010), towers (V001) and
  the tower-number function (V003) are the only existing objects it reads.

    dbo.CollectionsRcvRun      one row per refresh attempt of ONE company (history; counts, timings, error)
    dbo.CollectionsRcvCompany  one row per company: the CURRENT published run + the last attempt (state shown by the pages)
    dbo.CollectionsRcvLine     the published unpaid instalments (remaining Amount > 0), partitioned by RunId; readers only see Company.CurrentRunId

  Publishing never updates lines in place: a company's new lines are inserted under a new RunId and CurrentRunId is flipped in one transaction.
  A failed or abandoned attempt leaves the previous published run untouched.  Companies are independent in every table.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.CollectionsRcvRun', N'U') IS NULL
CREATE TABLE dbo.CollectionsRcvRun
(
    RunId                uniqueidentifier NOT NULL CONSTRAINT PK_CollectionsRcvRun PRIMARY KEY,
    CompanyId            int              NOT NULL CONSTRAINT CK_CollectionsRcvRun_Company CHECK (CompanyId IN (4, 32)),
    TriggerSource        nvarchar(50)     NOT NULL,
    StartedUtc           datetime2(3)     NOT NULL,
    FinishedUtc          datetime2(3)     NULL,
    Status               varchar(12)      NOT NULL CONSTRAINT CK_CollectionsRcvRun_Status CHECK (Status IN ('Running', 'Succeeded', 'Failed', 'Abandoned')),
    SourceFromDate       date             NULL,
    SourceThroughDate    date             NULL,
    SourceMinAmount      int              NULL,
    Layout               tinyint          NULL,       -- 1 = with ProjectCode (13 columns), 3 = without ProjectCode (12 columns)
    RawRows              int              NULL,       -- rows PACT returned
    PublishedRows        int              NULL,       -- unpaid instalments published
    ExcludedSettledRows  int              NULL,       -- remaining Amount <= 0
    ExcludedInvalidRows  int              NULL,       -- unpaid, but UnitID <= 0 / unit code blank or 0 / no customer / no due date
    ExcludedInvalidAmount decimal(19,4)   NULL,
    ExcludedCancelledRows int             NULL,       -- unpaid, but the unit code contains '*' (cancelled apartment)
    ExcludedCancelledAmount decimal(19,4) NULL,
    FetchMs              int              NULL,       -- INSERT ... EXEC from PACT (the remote part)
    PublishMs            int              NULL,
    ErrorNumber          int              NULL,
    ErrorMessage         nvarchar(1000)   NULL
);

/*
  dbo.CollectionsRcvAccountUnit - the unit/account DIRECTORY of a published run: one row per (company, tenant, unit) that PACT's receivables procedure returned, INCLUDING fully paid
  (settled) units and cancelled ('*') codes. It exists so a unit can be tied to its (CompanyId, TenantId, UnitId) and so the other units of a tenant account are known even when nothing is
  open on them. It is bound to the run exactly like CollectionsRcvLine. Completeness is NOT assumed: it holds only what the procedure returned (units without a payment plan / ledger row are
  absent), so the application treats it as complete scope evidence only when Collections:ReceivablesSnapshot:AccountDirectoryComplete is switched on after live verification.
*/
IF OBJECT_ID(N'dbo.CollectionsRcvAccountUnit', N'U') IS NULL
CREATE TABLE dbo.CollectionsRcvAccountUnit
(
    RunId        uniqueidentifier NOT NULL,
    CompanyId    int              NOT NULL,
    TenantId     nvarchar(100)    NOT NULL,
    UnitId       bigint           NOT NULL,
    UnitCode     nvarchar(200)    NOT NULL,
    OpenRows     int              NOT NULL,
    SettledRows  int              NOT NULL,
    IsCancelled  bit              NOT NULL,
    CONSTRAINT PK_CollectionsRcvAccountUnit PRIMARY KEY (RunId, CompanyId, TenantId, UnitId, UnitCode)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CollectionsRcvAccountUnit') AND name = N'IX_CollectionsRcvAccountUnit_Code')
    CREATE INDEX IX_CollectionsRcvAccountUnit_Code ON dbo.CollectionsRcvAccountUnit (CompanyId, UnitCode) INCLUDE (TenantId, UnitId, OpenRows, SettledRows, IsCancelled);
GO

IF OBJECT_ID(N'dbo.CollectionsRcvCompany', N'U') IS NULL
CREATE TABLE dbo.CollectionsRcvCompany
(
    CompanyId           int              NOT NULL CONSTRAINT PK_CollectionsRcvCompany PRIMARY KEY CONSTRAINT CK_CollectionsRcvCompany_Company CHECK (CompanyId IN (4, 32)),
    CurrentRunId        uniqueidentifier NULL,          -- NULL = never published: pages say "not loaded", never "zero receivables"
    LastSuccessUtc      datetime2(3)     NULL,
    PublishedRows       int              NOT NULL CONSTRAINT DF_CollectionsRcvCompany_Rows DEFAULT (0),
    LastRunId           uniqueidentifier NULL,
    LastAttemptUtc      datetime2(3)     NULL,          -- when the last attempt STARTED
    LastFinishedUtc     datetime2(3)     NULL,
    LastStatus          varchar(12)      NOT NULL CONSTRAINT DF_CollectionsRcvCompany_Status DEFAULT ('Never'),   -- Never | Running | Succeeded | Failed | Abandoned
    LastErrorNumber     int              NULL,
    LastError           nvarchar(1000)   NULL,
    ConsecutiveFailures int              NOT NULL CONSTRAINT DF_CollectionsRcvCompany_Failures DEFAULT (0)
);

MERGE dbo.CollectionsRcvCompany AS t
USING (VALUES (4), (32)) AS s (CompanyId) ON t.CompanyId = s.CompanyId
WHEN NOT MATCHED THEN INSERT (CompanyId) VALUES (s.CompanyId);

IF OBJECT_ID(N'dbo.CollectionsRcvLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CollectionsRcvLine
    (
        LineId        bigint IDENTITY(1,1) NOT NULL,
        RunId         uniqueidentifier NOT NULL,
        CompanyId     int              NOT NULL,
        TenantId      nvarchar(100)    COLLATE Latin1_General_BIN2 NOT NULL,   -- PACT customer (tenant) id
        UnitId        bigint           NOT NULL,                                -- always > 0
        UnitCode      nvarchar(200)    COLLATE Latin1_General_BIN2 NOT NULL,    -- never blank / 0 / containing '*'
        TowerNumber   nvarchar(20)     NULL,                                    -- dbo.fn_CollectionsTowerNumber(UnitCode)
        ProjectCode   nvarchar(200)    NOT NULL CONSTRAINT DF_CollectionsRcvLine_Project DEFAULT (N''),   -- '' for Sharjah (its procedure has no ProjectCode)
        FullName      nvarchar(400)    NOT NULL,
        Mobile        nvarchar(100)    NOT NULL,
        Email         nvarchar(400)    NOT NULL,
        VoucherNumber nvarchar(100)    NOT NULL,
        ChequeNumber  nvarchar(100)    NOT NULL,
        DueDate       date             NOT NULL,
        Amount        decimal(19,4)    NOT NULL,   -- REMAINING unpaid amount of the instalment, always > 0
        SourceStatus  nvarchar(50)     NULL,       -- PACT Status text (Paid / Installment ...); the procedures return no original or paid amount
        CONSTRAINT PK_CollectionsRcvLine PRIMARY KEY CLUSTERED (CompanyId, RunId, TenantId, UnitId, DueDate, LineId)
    );
    CREATE INDEX IX_CollectionsRcvLine_Due ON dbo.CollectionsRcvLine (CompanyId, RunId, DueDate) INCLUDE (TenantId, UnitId, Amount);
END;
PRINT N'R001 complete.';
