BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021120000_AddAdminEmailToCollectionsDispatch'
)
BEGIN
    ALTER TABLE [CollectionsDispatches] ADD [AdminEmailStatus] nvarchar(16) NOT NULL CONSTRAINT [DF_CollectionsDispatches_AdminEmailStatus] DEFAULT N'None';
    ALTER TABLE [CollectionsDispatches] ADD [AdminEmailAttemptedAtUtc] datetime2 NULL;
    ALTER TABLE [CollectionsDispatches] ADD [AdminEmailAttempts] int NOT NULL CONSTRAINT [DF_CollectionsDispatches_AdminEmailAttempts] DEFAULT 0;
    ALTER TABLE [CollectionsDispatches] ADD [AdminEmailRecipientCount] int NOT NULL CONSTRAINT [DF_CollectionsDispatches_AdminEmailRecipientCount] DEFAULT 0;
    ALTER TABLE [CollectionsDispatches] ADD [AdminEmailError] nvarchar(500) NULL;
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20261021120000_AddAdminEmailToCollectionsDispatch', N'10.0.0');
END;
COMMIT;
