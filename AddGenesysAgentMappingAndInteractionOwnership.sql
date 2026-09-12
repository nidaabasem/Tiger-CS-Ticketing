BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [GenesysAgentUserId] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [HandledByUserId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    ALTER TABLE [AspNetUsers] ADD [GenesysEmail] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    ALTER TABLE [AspNetUsers] ADD [GenesysUserId] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    CREATE INDEX [IX_TicketInteractions_HandledByUserId] ON [TicketInteractions] ([HandledByUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_AspNetUsers_GenesysUserId] ON [AspNetUsers] ([GenesysUserId]) WHERE [GenesysUserId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD CONSTRAINT [FK_TicketInteractions_AspNetUsers_HandledByUserId] FOREIGN KEY ([HandledByUserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260911105154_AddGenesysAgentMappingAndInteractionOwnership', N'10.0.11');
END;

COMMIT;
GO

