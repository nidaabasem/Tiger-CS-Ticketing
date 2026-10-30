BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030061255_AddTicketInteractionHandlers'
)
BEGIN
    CREATE TABLE [TicketInteractionHandlers] (
        [TicketInteractionHandlerId] bigint NOT NULL IDENTITY,
        [TicketInteractionId] bigint NOT NULL,
        [TicketId] bigint NOT NULL,
        [HandledByUserId] uniqueidentifier NOT NULL,
        [GenesysAgentUserId] nvarchar(64) NOT NULL,
        [Source] tinyint NOT NULL,
        [ConnectedAtUtc] datetime2 NULL,
        [RecordedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketInteractionHandlers] PRIMARY KEY ([TicketInteractionHandlerId]),
        CONSTRAINT [FK_TicketInteractionHandlers_AspNetUsers_HandledByUserId] FOREIGN KEY ([HandledByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketInteractionHandlers_TicketInteractions_TicketInteractionId] FOREIGN KEY ([TicketInteractionId]) REFERENCES [TicketInteractions] ([TicketInteractionId]) ON DELETE CASCADE,
        CONSTRAINT [FK_TicketInteractionHandlers_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030061255_AddTicketInteractionHandlers'
)
BEGIN
    CREATE INDEX [IX_TicketInteractionHandlers_HandledByUserId_TicketId] ON [TicketInteractionHandlers] ([HandledByUserId], [TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030061255_AddTicketInteractionHandlers'
)
BEGIN
    CREATE INDEX [IX_TicketInteractionHandlers_TicketId] ON [TicketInteractionHandlers] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030061255_AddTicketInteractionHandlers'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TicketInteractionHandlers_InteractionId_UserId] ON [TicketInteractionHandlers] ([TicketInteractionId], [HandledByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030061255_AddTicketInteractionHandlers'
)
BEGIN
    INSERT INTO [TicketInteractionHandlers]
        ([TicketInteractionId], [TicketId], [HandledByUserId], [GenesysAgentUserId], [Source], [ConnectedAtUtc], [RecordedAtUtc])
    SELECT i.[TicketInteractionId], i.[TicketId], i.[HandledByUserId], i.[GenesysAgentUserId], 9, NULL, i.[CreatedAtUtc]
    FROM [TicketInteractions] AS i
    WHERE i.[HandledByUserId] IS NOT NULL AND i.[GenesysAgentUserId] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030061255_AddTicketInteractionHandlers'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261030061255_AddTicketInteractionHandlers', N'10.0.11');
END;

COMMIT;
GO

