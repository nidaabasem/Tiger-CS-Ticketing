BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_AddGenesysServiceRequestToTicketInteractions'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [GenesysServiceRequestId] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_AddGenesysServiceRequestToTicketInteractions'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [RequiresHumanFollowUp] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_AddGenesysServiceRequestToTicketInteractions'
)
BEGIN
    DROP INDEX [UX_TicketInteractions_GenesysConversationId] ON [TicketInteractions];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_AddGenesysServiceRequestToTicketInteractions'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TicketInteractions_GenesysConversationId] ON [TicketInteractions] ([GenesysConversationId]) WHERE [GenesysConversationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_AddGenesysServiceRequestToTicketInteractions'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketInteractions_GenesysConversationId_ServiceRequestId] ON [TicketInteractions] ([GenesysConversationId], [GenesysServiceRequestId]) WHERE [GenesysConversationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022090000_AddGenesysServiceRequestToTicketInteractions'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022090000_AddGenesysServiceRequestToTicketInteractions', N'10.0.11');
END;

COMMIT;
GO

