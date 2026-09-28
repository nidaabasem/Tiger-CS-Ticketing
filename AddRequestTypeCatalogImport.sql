BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [WorkflowTemplateSteps] ADD [DepartmentId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [RequestTypeSlaPolicies] ADD [FirstResponseUnit] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [RequestTypes] ADD [Code] nvarchar(24) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [RequestTypes] ADD [Description] nvarchar(500) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [RequestTypes] ADD [RequestGroup] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [RequestTypes] ADD [RequiredDocumentsJson] nvarchar(2000) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    CREATE INDEX [IX_WorkflowTemplateSteps_DepartmentId] ON [WorkflowTemplateSteps] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_RequestTypes_Code] ON [RequestTypes] ([Code]) WHERE [Code] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    ALTER TABLE [WorkflowTemplateSteps] ADD CONSTRAINT [FK_WorkflowTemplateSteps_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928085727_AddRequestTypeCatalogImport'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260928085727_AddRequestTypeCatalogImport', N'10.0.11');
END;

COMMIT;
GO

