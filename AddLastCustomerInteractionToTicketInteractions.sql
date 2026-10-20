BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261020105026_AddLastCustomerInteractionToTicketInteractions'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [LastCustomerInteractionAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261020105026_AddLastCustomerInteractionToTicketInteractions'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [LastCustomerInteractionReportedByEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261020105026_AddLastCustomerInteractionToTicketInteractions'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TicketInteractions_LastCustomerInteractionAtUtc] ON [TicketInteractions] ([LastCustomerInteractionAtUtc]) WHERE [LastCustomerInteractionAtUtc] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261020105026_AddLastCustomerInteractionToTicketInteractions'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261020105026_AddLastCustomerInteractionToTicketInteractions', N'10.0.11');
END;

COMMIT;
GO

