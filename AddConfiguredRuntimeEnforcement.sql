BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CurrentWorkflowStepId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    ALTER TABLE [RequestTypes] ADD [ConfigurationEnforced] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    CREATE TABLE [RequestTypeCatalogDecisions] (
        [RequestTypeCatalogDecisionId] int NOT NULL IDENTITY,
        [RequestTypeId] int NOT NULL,
        [Area] nvarchar(40) NOT NULL,
        [Question] nvarchar(1000) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [ResolvedAtUtc] datetime2 NULL,
        [ResolvedByEmployeeId] uniqueidentifier NULL,
        [Resolution] nvarchar(1000) NULL,
        CONSTRAINT [PK_RequestTypeCatalogDecisions] PRIMARY KEY ([RequestTypeCatalogDecisionId]),
        CONSTRAINT [FK_RequestTypeCatalogDecisions_RequestTypes_RequestTypeId] FOREIGN KEY ([RequestTypeId]) REFERENCES [RequestTypes] ([RequestTypeId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    CREATE INDEX [IX_Tickets_CurrentWorkflowStepId] ON [Tickets] ([CurrentWorkflowStepId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    CREATE INDEX [IX_RequestTypeCatalogDecisions_RequestTypeId] ON [RequestTypeCatalogDecisions] ([RequestTypeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    ALTER TABLE [Tickets] ADD CONSTRAINT [FK_Tickets_WorkflowTemplateSteps_CurrentWorkflowStepId] FOREIGN KEY ([CurrentWorkflowStepId]) REFERENCES [WorkflowTemplateSteps] ([WorkflowTemplateStepId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928102230_AddConfiguredRuntimeEnforcement'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260928102230_AddConfiguredRuntimeEnforcement', N'10.0.11');
END;

COMMIT;
GO

