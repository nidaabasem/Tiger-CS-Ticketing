BEGIN TRANSACTION;
CREATE TABLE [Workflows] (
    [WorkflowId] int NOT NULL IDENTITY,
    [Code] nvarchar(24) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    [Description] nvarchar(500) NULL,
    [IsActive] bit NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_Workflows] PRIMARY KEY ([WorkflowId])
);

CREATE UNIQUE INDEX [IX_Workflows_Code] ON [Workflows] ([Code]);

INSERT INTO [Workflows] ([Code], [Name], [Description], [IsActive], [CreatedAtUtc])
SELECT [Code], [Name], [Description], [IsActive], SYSUTCDATETIME()
FROM [WorkflowTemplates]
ORDER BY [WorkflowTemplateId];

ALTER TABLE [WorkflowTemplates] ADD [WorkflowId] int NULL;

ALTER TABLE [WorkflowTemplates] ADD [VersionNumber] int NULL;

ALTER TABLE [WorkflowTemplates] ADD [Status] tinyint NULL;

ALTER TABLE [WorkflowTemplates] ADD [CreatedAtUtc] datetime2 NULL;

ALTER TABLE [WorkflowTemplates] ADD [CreatedByEmployeeId] uniqueidentifier NULL;

ALTER TABLE [WorkflowTemplates] ADD [PublishedAtUtc] datetime2 NULL;

ALTER TABLE [WorkflowTemplates] ADD [PublishedByEmployeeId] uniqueidentifier NULL;

UPDATE t
SET t.[WorkflowId] = w.[WorkflowId],
    t.[VersionNumber] = 1,
    t.[Status] = 2,
    t.[CreatedAtUtc] = SYSUTCDATETIME(),
    t.[PublishedAtUtc] = SYSUTCDATETIME()
FROM [WorkflowTemplates] t
INNER JOIN [Workflows] w ON w.[Code] = t.[Code];

DECLARE @var nvarchar(max);
SELECT @var = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'WorkflowId');
IF @var IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var + ';');
ALTER TABLE [WorkflowTemplates] ALTER COLUMN [WorkflowId] int NOT NULL;

DECLARE @var1 nvarchar(max);
SELECT @var1 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'VersionNumber');
IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var1 + ';');
ALTER TABLE [WorkflowTemplates] ALTER COLUMN [VersionNumber] int NOT NULL;

DECLARE @var2 nvarchar(max);
SELECT @var2 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'Status');
IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var2 + ';');
ALTER TABLE [WorkflowTemplates] ALTER COLUMN [Status] tinyint NOT NULL;

DECLARE @var3 nvarchar(max);
SELECT @var3 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'CreatedAtUtc');
IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var3 + ';');
ALTER TABLE [WorkflowTemplates] ALTER COLUMN [CreatedAtUtc] datetime2 NOT NULL;

ALTER TABLE [RequestTypes] DROP CONSTRAINT [FK_RequestTypes_WorkflowTemplates_WorkflowTemplateId];

ALTER TABLE [Tickets] ADD [WorkflowTemplateId] int NULL;

UPDATE tk
SET tk.[WorkflowTemplateId] = rt.[WorkflowTemplateId]
FROM [Tickets] tk
INNER JOIN [RequestTypes] rt ON rt.[RequestTypeId] = tk.[RequestTypeId]
WHERE tk.[RequestTypeId] IS NOT NULL
  AND tk.[WorkflowTemplateId] IS NULL;

EXEC sp_rename N'[RequestTypes].[WorkflowTemplateId]', N'WorkflowId', 'COLUMN';

EXEC sp_rename N'[RequestTypes].[IX_RequestTypes_WorkflowTemplateId]', N'IX_RequestTypes_WorkflowId', 'INDEX';

UPDATE rt
SET rt.[WorkflowId] = t.[WorkflowId]
FROM [RequestTypes] rt
INNER JOIN [WorkflowTemplates] t ON t.[WorkflowTemplateId] = rt.[WorkflowId];

DROP INDEX [IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence] ON [WorkflowTemplateSteps];

ALTER TABLE [WorkflowTemplateSteps] ADD [ApprovalType] tinyint NULL;

CREATE TABLE [WorkflowStepTransitions] (
    [WorkflowStepTransitionId] int NOT NULL IDENTITY,
    [WorkflowTemplateStepId] int NOT NULL,
    [Outcome] tinyint NOT NULL,
    [TargetWorkflowTemplateStepId] int NOT NULL,
    CONSTRAINT [PK_WorkflowStepTransitions] PRIMARY KEY ([WorkflowStepTransitionId]),
    CONSTRAINT [FK_WorkflowStepTransitions_WorkflowTemplateSteps_TargetWorkflowTemplateStepId] FOREIGN KEY ([TargetWorkflowTemplateStepId]) REFERENCES [WorkflowTemplateSteps] ([WorkflowTemplateStepId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_WorkflowStepTransitions_WorkflowTemplateSteps_WorkflowTemplateStepId] FOREIGN KEY ([WorkflowTemplateStepId]) REFERENCES [WorkflowTemplateSteps] ([WorkflowTemplateStepId]) ON DELETE CASCADE
);

CREATE INDEX [IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence] ON [WorkflowTemplateSteps] ([WorkflowTemplateId], [Sequence]);

CREATE INDEX [IX_WorkflowTemplates_CreatedByEmployeeId] ON [WorkflowTemplates] ([CreatedByEmployeeId]);

CREATE INDEX [IX_WorkflowTemplates_PublishedByEmployeeId] ON [WorkflowTemplates] ([PublishedByEmployeeId]);

CREATE UNIQUE INDEX [IX_WorkflowTemplates_WorkflowId_VersionNumber] ON [WorkflowTemplates] ([WorkflowId], [VersionNumber]);

CREATE UNIQUE INDEX [UX_WorkflowTemplates_OneDraftPerWorkflow] ON [WorkflowTemplates] ([WorkflowId]) WHERE [Status] = 1;

CREATE UNIQUE INDEX [UX_WorkflowTemplates_OnePublishedPerWorkflow] ON [WorkflowTemplates] ([WorkflowId]) WHERE [Status] = 2;

CREATE INDEX [IX_Tickets_WorkflowTemplateId] ON [Tickets] ([WorkflowTemplateId]);

CREATE INDEX [IX_WorkflowStepTransitions_TargetWorkflowTemplateStepId] ON [WorkflowStepTransitions] ([TargetWorkflowTemplateStepId]);

CREATE UNIQUE INDEX [IX_WorkflowStepTransitions_WorkflowTemplateStepId_Outcome] ON [WorkflowStepTransitions] ([WorkflowTemplateStepId], [Outcome]);

ALTER TABLE [RequestTypes] ADD CONSTRAINT [FK_RequestTypes_Workflows_WorkflowId] FOREIGN KEY ([WorkflowId]) REFERENCES [Workflows] ([WorkflowId]) ON DELETE NO ACTION;

ALTER TABLE [Tickets] ADD CONSTRAINT [FK_Tickets_WorkflowTemplates_WorkflowTemplateId] FOREIGN KEY ([WorkflowTemplateId]) REFERENCES [WorkflowTemplates] ([WorkflowTemplateId]) ON DELETE NO ACTION;

ALTER TABLE [WorkflowTemplates] ADD CONSTRAINT [FK_WorkflowTemplates_Employees_CreatedByEmployeeId] FOREIGN KEY ([CreatedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION;

ALTER TABLE [WorkflowTemplates] ADD CONSTRAINT [FK_WorkflowTemplates_Employees_PublishedByEmployeeId] FOREIGN KEY ([PublishedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION;

ALTER TABLE [WorkflowTemplates] ADD CONSTRAINT [FK_WorkflowTemplates_Workflows_WorkflowId] FOREIGN KEY ([WorkflowId]) REFERENCES [Workflows] ([WorkflowId]) ON DELETE NO ACTION;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260908054944_AddWorkflowVersioning', N'10.0.11');

COMMIT;
GO

