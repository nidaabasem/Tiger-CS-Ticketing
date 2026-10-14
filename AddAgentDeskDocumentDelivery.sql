BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[CrmDocumentDeliveryRequests]') AND [c].[name] = N'VerificationSessionId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [CrmDocumentDeliveryRequests] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [CrmDocumentDeliveryRequests] ALTER COLUMN [VerificationSessionId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    ALTER TABLE [CrmDocumentDeliveryRequests] ADD [CrmCustomerId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    ALTER TABLE [CrmDocumentDeliveryRequests] ADD [CrmLeadId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    ALTER TABLE [CrmDocumentDeliveryRequests] ADD [Origin] tinyint NOT NULL DEFAULT CAST(1 AS tinyint);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    CREATE INDEX [IX_CrmDocumentDeliveryRequests_AgentDocument] ON [CrmDocumentDeliveryRequests] ([CrmCustomerId], [CrmLeadId], [DocumentType], [CrmRecordId], [Channel], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    EXEC(N'ALTER TABLE [CrmDocumentDeliveryRequests] ADD CONSTRAINT [CK_CrmDocumentDeliveryRequests_Origin] CHECK (([Origin] = 1 AND [VerificationSessionId] IS NOT NULL AND [CrmCustomerId] IS NULL AND [CrmLeadId] IS NULL) OR ([Origin] = 2 AND [VerificationSessionId] IS NULL AND [CrmCustomerId] IS NOT NULL AND [CrmLeadId] IS NOT NULL))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261012124810_AddAgentDeskDocumentDelivery'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261012124810_AddAgentDeskDocumentDelivery', N'10.0.11');
END;

COMMIT;
GO

