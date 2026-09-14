BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912110119_AddExternalCustomerSnapshotToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ExternalCustomerEmail] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912110119_AddExternalCustomerSnapshotToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ExternalCustomerName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260912110119_AddExternalCustomerSnapshotToTickets'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260912110119_AddExternalCustomerSnapshotToTickets', N'10.0.11');
END;

COMMIT;
GO

