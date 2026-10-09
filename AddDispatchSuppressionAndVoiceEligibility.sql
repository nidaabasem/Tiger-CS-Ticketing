BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[CollectionsDispatches]') AND [c].[name] = N'AcknowledgedSharedPhoneCalls');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [CollectionsDispatches] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [CollectionsDispatches] DROP COLUMN [AcknowledgedSharedPhoneCalls];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [BalanceCheckedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [SuppressedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [SuppressionError] nvarchar(500) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [SuppressionStatus] nvarchar(16) NOT NULL DEFAULT N'None';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatchItems] ADD [VoiceEligible] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatches] ADD [Phase] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    ALTER TABLE [CollectionsDispatches] ADD [RevalidationMs] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261009074521_AddDispatchSuppressionAndVoiceEligibility'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261009074521_AddDispatchSuppressionAndVoiceEligibility', N'10.0.11');
END;

COMMIT;
GO

