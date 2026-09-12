BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    DROP INDEX [IX_TicketInteractions_GenesysConversationId] ON [TicketInteractions];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Tickets]') AND [c].[name] = N'PriorityId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Tickets] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Tickets] ALTER COLUMN [PriorityId] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Tickets]') AND [c].[name] = N'CategoryId');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Tickets] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [Tickets] ALTER COLUMN [CategoryId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [CustomerEmail] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [CustomerName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [EndReason] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [EndedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE TABLE [GenesysQueueMappings] (
        [GenesysQueueMappingId] int NOT NULL IDENTITY,
        [QueueId] nvarchar(64) NOT NULL,
        [QueueName] nvarchar(200) NULL,
        [DepartmentId] int NOT NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_GenesysQueueMappings] PRIMARY KEY ([GenesysQueueMappingId]),
        CONSTRAINT [FK_GenesysQueueMappings_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE TABLE [TicketAgentHandoffs] (
        [TicketAgentHandoffId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [TicketInteractionId] bigint NOT NULL,
        [DepartmentId] int NOT NULL,
        [ChannelId] tinyint NOT NULL,
        [Status] tinyint NOT NULL,
        [Mode] tinyint NULL,
        [RequestReason] nvarchar(500) NULL,
        [ExternalWorkItemId] nvarchar(64) NULL,
        [RequestedAtUtc] datetime2 NOT NULL,
        [AssignedEmployeeId] uniqueidentifier NULL,
        [GenesysAgentId] nvarchar(64) NULL,
        [AssignedAtUtc] datetime2 NULL,
        [StartedAtUtc] datetime2 NULL,
        [CompletedAtUtc] datetime2 NULL,
        [ResolvedAtUtc] datetime2 NULL,
        [ResolutionNote] nvarchar(500) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketAgentHandoffs] PRIMARY KEY ([TicketAgentHandoffId]),
        CONSTRAINT [FK_TicketAgentHandoffs_Channels_ChannelId] FOREIGN KEY ([ChannelId]) REFERENCES [Channels] ([ChannelId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAgentHandoffs_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAgentHandoffs_TicketInteractions_TicketInteractionId] FOREIGN KEY ([TicketInteractionId]) REFERENCES [TicketInteractions] ([TicketInteractionId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAgentHandoffs_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE TABLE [TicketInteractionMessages] (
        [TicketInteractionMessageId] bigint NOT NULL IDENTITY,
        [TicketInteractionId] bigint NOT NULL,
        [Sequence] int NOT NULL,
        [Sender] tinyint NOT NULL,
        [SenderName] nvarchar(200) NULL,
        [SenderId] nvarchar(64) NULL,
        [ExternalMessageId] nvarchar(64) NULL,
        [SentAtUtc] datetime2 NOT NULL,
        [Body] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_TicketInteractionMessages] PRIMARY KEY ([TicketInteractionMessageId]),
        CONSTRAINT [FK_TicketInteractionMessages_TicketInteractions_TicketInteractionId] FOREIGN KEY ([TicketInteractionId]) REFERENCES [TicketInteractions] ([TicketInteractionId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketInteractions_GenesysConversationId] ON [TicketInteractions] ([GenesysConversationId]) WHERE [GenesysConversationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE INDEX [IX_GenesysQueueMappings_DepartmentId] ON [GenesysQueueMappings] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_GenesysQueueMappings_QueueId] ON [GenesysQueueMappings] ([QueueId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE INDEX [IX_TicketAgentHandoffs_AssignedEmployeeId] ON [TicketAgentHandoffs] ([AssignedEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE INDEX [IX_TicketAgentHandoffs_ChannelId] ON [TicketAgentHandoffs] ([ChannelId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TicketAgentHandoffs_OpenByDepartment] ON [TicketAgentHandoffs] ([DepartmentId], [RequestedAtUtc]) WHERE [ResolvedAtUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE INDEX [IX_TicketAgentHandoffs_TicketId] ON [TicketAgentHandoffs] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketAgentHandoffs_ExternalWorkItemId] ON [TicketAgentHandoffs] ([ExternalWorkItemId]) WHERE [ExternalWorkItemId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketAgentHandoffs_OpenPerInteraction] ON [TicketAgentHandoffs] ([TicketInteractionId]) WHERE [ResolvedAtUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TicketInteractionMessages_InteractionSequence] ON [TicketInteractionMessages] ([TicketInteractionId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910080219_AddGenesysIntegration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260910080219_AddGenesysIntegration', N'10.0.11');
END;

COMMIT;
GO

