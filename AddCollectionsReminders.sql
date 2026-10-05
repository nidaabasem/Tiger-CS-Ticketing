BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE TABLE [CollectionsReminders] (
        [CollectionsReminderId] bigint NOT NULL IDENTITY,
        [CrmCustomerId] bigint NOT NULL,
        [AccountId] nvarchar(64) NOT NULL,
        [UnitId] bigint NULL,
        [Type] nvarchar(24) NOT NULL,
        [CycleKey] nvarchar(40) NOT NULL,
        [Currency] char(3) NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [AmountBasis] nvarchar(80) NOT NULL,
        [InstalmentIds] nvarchar(1000) NOT NULL,
        [SourceAsOfUtc] datetime2 NOT NULL,
        [Language] varchar(2) NOT NULL,
        [Trigger] nvarchar(16) NOT NULL,
        [RequestedByEmployeeId] uniqueidentifier NULL,
        [IdempotencyKey] nvarchar(128) NULL,
        [RequestHash] char(64) NULL,
        [QueuedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_CollectionsReminders] PRIMARY KEY ([CollectionsReminderId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE TABLE [CollectionsReminderChannels] (
        [CollectionsReminderChannelId] bigint NOT NULL IDENTITY,
        [CollectionsReminderId] bigint NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [DeduplicationKey] nvarchar(200) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [StatusReason] nvarchar(500) NULL,
        [ProviderMessageId] nvarchar(128) NULL,
        [LastEventAtUtc] datetime2 NULL,
        [Attempts] int NOT NULL,
        [DispatchAmount] decimal(19,4) NULL,
        [DispatchSourceAsOfUtc] datetime2 NULL,
        CONSTRAINT [PK_CollectionsReminderChannels] PRIMARY KEY ([CollectionsReminderChannelId]),
        CONSTRAINT [FK_CollectionsReminderChannels_CollectionsReminders_CollectionsReminderId] FOREIGN KEY ([CollectionsReminderId]) REFERENCES [CollectionsReminders] ([CollectionsReminderId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE TABLE [CollectionsReminderEvents] (
        [CollectionsReminderEventId] bigint NOT NULL IDENTITY,
        [CollectionsReminderId] bigint NOT NULL,
        [ExternalEventId] nvarchar(128) NOT NULL,
        [IdempotencyKey] nvarchar(128) NULL,
        [RequestHash] char(64) NULL,
        [Channel] nvarchar(16) NULL,
        [DeliveryStatus] nvarchar(16) NULL,
        [ProviderMessageId] nvarchar(128) NULL,
        [ConversationId] nvarchar(128) NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [RecordedAtUtc] datetime2 NOT NULL,
        [ReportedByEmployeeId] uniqueidentifier NULL,
        [Detail] nvarchar(1000) NULL,
        [CustomerResponded] bit NOT NULL,
        [CustomerIntent] nvarchar(24) NULL,
        [CustomerPhone] nvarchar(32) NULL,
        [RequiresHumanFollowUp] bit NOT NULL,
        [VerificationFollowUpRequired] bit NOT NULL,
        [TicketResult] nvarchar(16) NOT NULL,
        [TicketId] bigint NULL,
        [TicketNumber] nvarchar(64) NULL,
        [TicketLinkedAtUtc] datetime2 NULL,
        [TicketAttempts] int NOT NULL,
        [TicketLastError] nvarchar(500) NULL,
        CONSTRAINT [PK_CollectionsReminderEvents] PRIMARY KEY ([CollectionsReminderEventId]),
        CONSTRAINT [FK_CollectionsReminderEvents_CollectionsReminders_CollectionsReminderId] FOREIGN KEY ([CollectionsReminderId]) REFERENCES [CollectionsReminders] ([CollectionsReminderId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE INDEX [IX_CollectionsReminderChannels_CollectionsReminderId] ON [CollectionsReminderChannels] ([CollectionsReminderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsReminderChannels_DeduplicationKey] ON [CollectionsReminderChannels] ([DeduplicationKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE INDEX [IX_CollectionsReminderEvents_TicketId] ON [CollectionsReminderEvents] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsReminderEvents_Reminder_EventId] ON [CollectionsReminderEvents] ([CollectionsReminderId], [ExternalEventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    CREATE INDEX [IX_CollectionsReminders_Customer_Queued] ON [CollectionsReminders] ([CrmCustomerId], [QueuedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_CollectionsReminders_IdempotencyKey] ON [CollectionsReminders] ([IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005072511_AddCollectionsReminders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005072511_AddCollectionsReminders', N'10.0.11');
END;

COMMIT;
GO

