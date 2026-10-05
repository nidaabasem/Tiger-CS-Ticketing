BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    CREATE TABLE [CollectionsReminders] (
        [CollectionsReminderId] bigint NOT NULL IDENTITY,
        [CrmCustomerId] nvarchar(64) NOT NULL,
        [AccountId] nvarchar(64) NOT NULL,
        [CrmUnitId] nvarchar(64) NULL,
        [Type] nvarchar(32) NOT NULL,
        [Channel] nvarchar(16) NOT NULL,
        [CycleKey] nvarchar(10) NOT NULL,
        [DeduplicationKey] nvarchar(256) NOT NULL,
        [Currency] char(3) NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [AmountIncludesFines] bit NOT NULL,
        [SourceAsOfUtc] datetime2 NOT NULL,
        [DispatchAmount] decimal(19,4) NULL,
        [DispatchSourceAsOfUtc] datetime2 NULL,
        [Status] nvarchar(16) NOT NULL,
        [StatusReason] nvarchar(500) NULL,
        [ProviderReference] nvarchar(128) NULL,
        [Trigger] nvarchar(16) NOT NULL,
        [RequestedByEmployeeId] uniqueidentifier NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [SentAtUtc] datetime2 NULL,
        [DeliveredAtUtc] datetime2 NULL,
        [FailedAtUtc] datetime2 NULL,
        [SuppressedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CollectionsReminders] PRIMARY KEY ([CollectionsReminderId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    CREATE TABLE [CollectionsReminderEvents] (
        [CollectionsReminderEventId] bigint NOT NULL IDENTITY,
        [CollectionsReminderId] bigint NOT NULL,
        [ExternalEventId] nvarchar(128) NOT NULL,
        [EventType] nvarchar(24) NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [RecordedAtUtc] datetime2 NOT NULL,
        [ReportedByEmployeeId] uniqueidentifier NULL,
        [Detail] nvarchar(1000) NULL,
        [ResponseKind] nvarchar(24) NULL,
        [ConversationId] nvarchar(128) NULL,
        [CustomerPhone] nvarchar(32) NULL,
        [PromisedPaymentDate] date NULL,
        [PromisedAmount] decimal(19,4) NULL,
        [VerificationFollowUpRequired] bit NOT NULL,
        [HumanFollowUpRequired] bit NOT NULL,
        [TicketStatus] nvarchar(16) NULL,
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
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    CREATE INDEX [IX_CollectionsReminderEvents_TicketId] ON [CollectionsReminderEvents] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsReminderEvents_Reminder_EventId] ON [CollectionsReminderEvents] ([CollectionsReminderId], [ExternalEventId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    CREATE INDEX [IX_CollectionsReminders_Customer_Created] ON [CollectionsReminders] ([CrmCustomerId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CollectionsReminders_DeduplicationKey] ON [CollectionsReminders] ([DeduplicationKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261005064542_AddCollectionsReminders'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261005064542_AddCollectionsReminders', N'10.0.11');
END;

COMMIT;
GO

