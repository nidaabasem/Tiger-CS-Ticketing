BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007103153_AddResolutionClosedForCustomerInactivity'
)
BEGIN
    ALTER TABLE [TicketResolutions] ADD [ClosedForCustomerInactivity] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007103153_AddResolutionClosedForCustomerInactivity'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007103153_AddResolutionClosedForCustomerInactivity', N'10.0.11');
END;

COMMIT;
GO

