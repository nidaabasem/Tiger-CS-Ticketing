BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE TABLE [CollectionsDispatches] (
        [CollectionsDispatchId] bigint NOT NULL IDENTITY,
        [PublicId] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(100) NOT NULL,
        [Fingerprint] char(64) NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [InitiatedByEmployeeId] uniqueidentifier NOT NULL,
        [InitiatedAtUtc] datetime2 NOT NULL,
        [ReviewRunId] bigint NOT NULL,
        [SelectionMode] nvarchar(24) NOT NULL,
        [FilterJson] nvarchar(2000) NOT NULL,
        [ApprovedCount] int NOT NULL,
        [ApprovedTotalsJson] nvarchar(500) NOT NULL,
        [AcknowledgedActiveCampaignRisk] bit NOT NULL,
        [AcknowledgedSharedPhoneCalls] bit NOT NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [StatusReason] nvarchar(500) NULL,
        [ExcludedAtDispatchCount] int NOT NULL,
        [LeaseOwner] uniqueidentifier NULL,
        [LeaseExpiresAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CollectionsDispatches] PRIMARY KEY ([CollectionsDispatchId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE TABLE [CollectionsReviewRuns] (
        [CollectionsReviewRunId] bigint NOT NULL IDENTITY,
        [Status] nvarchar(16) NOT NULL,
        [AsOfDate] date NOT NULL,
        [CompanyId] int NULL,
        [DueFrom] date NOT NULL,
        [DueTo] date NOT NULL,
        [Source] nvarchar(200) NOT NULL,
        [SourceProcedureSuffix] varchar(16) NOT NULL,
        [SourceReconciled] bit NOT NULL,
        [RequestedByEmployeeId] uniqueidentifier NOT NULL,
        [RequestedAtUtc] datetime2 NOT NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [SourceReadAtUtc] datetime2 NULL,
        [Phase] nvarchar(100) NOT NULL,
        [ProgressPercent] int NOT NULL,
        [SourceRowCount] int NOT NULL,
        [RecordCount] int NOT NULL,
        [Error] nvarchar(500) NULL,
        [IsCurrent] bit NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_CollectionsReviewRuns] PRIMARY KEY ([CollectionsReviewRunId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE TABLE [CollectionsDispatchItems] (
        [CollectionsDispatchItemId] bigint NOT NULL IDENTITY,
        [CollectionsDispatchId] bigint NOT NULL,
        [RecordKey] char(64) NOT NULL,
        [ReminderType] nvarchar(16) NOT NULL,
        [CompanyId] int NOT NULL,
        [TenantId] nvarchar(50) NOT NULL,
        [UnitId] int NULL,
        [UnitCode] nvarchar(60) NOT NULL,
        [CustomerName] nvarchar(300) NOT NULL,
        [Phone] varchar(20) NOT NULL,
        [Email] nvarchar(320) NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [Currency] char(3) NOT NULL,
        [DueDate] date NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [StatusReason] nvarchar(500) NULL,
        [CollectionsGenesysBatchId] bigint NULL,
        [BatchPosition] int NULL,
        [GenesysContactId] varchar(64) NULL,
        [UploadedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CollectionsDispatchItems] PRIMARY KEY ([CollectionsDispatchItemId]),
        CONSTRAINT [FK_CollectionsDispatchItems_CollectionsDispatches_CollectionsDispatchId] FOREIGN KEY ([CollectionsDispatchId]) REFERENCES [CollectionsDispatches] ([CollectionsDispatchId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE TABLE [CollectionsGenesysBatches] (
        [CollectionsGenesysBatchId] bigint NOT NULL IDENTITY,
        [CollectionsDispatchId] bigint NOT NULL,
        [ReminderType] nvarchar(16) NOT NULL,
        [ContactListId] varchar(36) NOT NULL,
        [Sequence] int NOT NULL,
        [ContactCount] int NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [AttemptCount] int NOT NULL,
        [RequestHash] varchar(64) NULL,
        [HttpStatus] int NULL,
        [ReturnedContactCount] int NULL,
        [Error] nvarchar(500) NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [ReconciledByEmployeeId] uniqueidentifier NULL,
        [ReconciledAtUtc] datetime2 NULL,
        [ReconciliationNote] nvarchar(500) NULL,
        CONSTRAINT [PK_CollectionsGenesysBatches] PRIMARY KEY ([CollectionsGenesysBatchId]),
        CONSTRAINT [FK_CollectionsGenesysBatches_CollectionsDispatches_CollectionsDispatchId] FOREIGN KEY ([CollectionsDispatchId]) REFERENCES [CollectionsDispatches] ([CollectionsDispatchId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE TABLE [CollectionsReviewRecords] (
        [CollectionsReviewRecordId] bigint NOT NULL IDENTITY,
        [CollectionsReviewRunId] bigint NOT NULL,
        [RecordKey] char(64) NOT NULL,
        [CycleKey] varchar(40) NOT NULL,
        [ReminderType] nvarchar(16) NOT NULL,
        [CompanyId] int NOT NULL,
        [TenantId] nvarchar(50) NOT NULL,
        [CustomerName] nvarchar(300) NOT NULL,
        [Phone] varchar(20) NOT NULL,
        [Email] nvarchar(320) NOT NULL,
        [UnitId] int NULL,
        [UnitCode] nvarchar(60) NOT NULL,
        [ProjectCode] nvarchar(60) NOT NULL,
        [PaymentStatus] nvarchar(16) NOT NULL,
        [SourceStatus] nvarchar(100) NOT NULL,
        [RemainingAmount] decimal(19,4) NULL,
        [RawRemainingAmount] decimal(19,4) NULL,
        [Currency] char(3) NOT NULL,
        [DueDate] date NULL,
        [InstalmentCount] int NOT NULL,
        [ValidationStatus] nvarchar(16) NOT NULL,
        [Reasons] nvarchar(600) NOT NULL,
        [SourceReadAtUtc] datetime2 NOT NULL,
        [Source] nvarchar(200) NOT NULL,
        CONSTRAINT [PK_CollectionsReviewRecords] PRIMARY KEY ([CollectionsReviewRecordId]),
        CONSTRAINT [FK_CollectionsReviewRecords_CollectionsReviewRuns_CollectionsReviewRunId] FOREIGN KEY ([CollectionsReviewRunId]) REFERENCES [CollectionsReviewRuns] ([CollectionsReviewRunId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsDispatches_IdempotencyKey] ON [CollectionsDispatches] ([IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsDispatches_PublicId] ON [CollectionsDispatches] ([PublicId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE INDEX [IX_CollectionsDispatchItems_Batch] ON [CollectionsDispatchItems] ([CollectionsGenesysBatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE INDEX [IX_CollectionsDispatchItems_CollectionsDispatchId] ON [CollectionsDispatchItems] ([CollectionsDispatchId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE INDEX [IX_CollectionsDispatchItems_RecordKey] ON [CollectionsDispatchItems] ([RecordKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CollectionsDispatchItems_LiveRecord] ON [CollectionsDispatchItems] ([RecordKey]) WHERE [Status] IN (''Approved'',''UploadedToGenesys'',''UnknownOutcome'')');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsGenesysBatches_Dispatch_Sequence] ON [CollectionsGenesysBatches] ([CollectionsDispatchId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE INDEX [IX_CollectionsReviewRecords_Run_Customer] ON [CollectionsReviewRecords] ([CollectionsReviewRunId], [CompanyId], [TenantId], [UnitCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE INDEX [IX_CollectionsReviewRecords_Run_Due] ON [CollectionsReviewRecords] ([CollectionsReviewRunId], [DueDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE INDEX [IX_CollectionsReviewRecords_Run_Type_Status] ON [CollectionsReviewRecords] ([CollectionsReviewRunId], [ReminderType], [ValidationStatus]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsReviewRecords_Run_Key] ON [CollectionsReviewRecords] ([CollectionsReviewRunId], [RecordKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CollectionsReviewRuns_Active] ON [CollectionsReviewRuns] ([IsActive]) WHERE [IsActive] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CollectionsReviewRuns_Current] ON [CollectionsReviewRuns] ([IsCurrent]) WHERE [IsCurrent] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009065134_AddCollectionsReviewAndGenesysDispatch'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009065134_AddCollectionsReviewAndGenesysDispatch', N'10.0.11');
END;

COMMIT;
GO

