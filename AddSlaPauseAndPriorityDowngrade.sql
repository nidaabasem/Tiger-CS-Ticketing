BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    ALTER TABLE [TicketSlaInstances] ADD [AppliedFirstResponseTargetMinutes] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    ALTER TABLE [TicketSlaInstances] ADD [AppliedResolutionTargetMinutes] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    ALTER TABLE [TicketSlaInstances] ADD [PausesOnPendingCustomerOverride] bit NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    ALTER TABLE [TicketSlaInstances] ADD [RequestTypeSlaNote] nvarchar(500) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    ALTER TABLE [TicketSlaInstances] ADD [RequestTypeSlaPolicyId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    ALTER TABLE [TicketSlaInstances] ADD [ResolutionClockBasis] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE TABLE [PriorityDowngradeRequests] (
        [PriorityDowngradeRequestId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [CurrentPriorityId] tinyint NOT NULL,
        [RequestedPriorityId] tinyint NOT NULL,
        [Reason] nvarchar(1000) NOT NULL,
        [Status] tinyint NOT NULL,
        [RequestedByEmployeeId] uniqueidentifier NOT NULL,
        [RequestedAtUtc] datetime2 NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [DecidedByEmployeeId] uniqueidentifier NULL,
        [DecidedAtUtc] datetime2 NULL,
        [DecisionNote] nvarchar(1000) NULL,
        [RowVersion] rowversion NOT NULL,
        CONSTRAINT [PK_PriorityDowngradeRequests] PRIMARY KEY ([PriorityDowngradeRequestId]),
        CONSTRAINT [CK_PriorityDowngradeRequests_DecisionConsistent] CHECK (([Status] IN (2, 3) AND [DecidedByEmployeeId] IS NOT NULL AND [DecidedByEmployeeId] <> [RequestedByEmployeeId]) OR [Status] NOT IN (2, 3)),
        CONSTRAINT [CK_PriorityDowngradeRequests_IsDowngrade] CHECK ([RequestedPriorityId] > [CurrentPriorityId]),
        CONSTRAINT [FK_PriorityDowngradeRequests_Employees_DecidedByEmployeeId] FOREIGN KEY ([DecidedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PriorityDowngradeRequests_Employees_RequestedByEmployeeId] FOREIGN KEY ([RequestedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PriorityDowngradeRequests_Priorities_CurrentPriorityId] FOREIGN KEY ([CurrentPriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PriorityDowngradeRequests_Priorities_RequestedPriorityId] FOREIGN KEY ([RequestedPriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_PriorityDowngradeRequests_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE TABLE [TicketSlaPausePeriods] (
        [TicketSlaPausePeriodId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [TicketSlaInstanceId] bigint NOT NULL,
        [Reason] tinyint NOT NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [ResumedAtUtc] datetime2 NULL,
        [ResolutionDueBeforeAtUtc] datetime2 NOT NULL,
        [ResolutionDueAfterAtUtc] datetime2 NULL,
        [EndedByResolution] bit NOT NULL,
        CONSTRAINT [PK_TicketSlaPausePeriods] PRIMARY KEY ([TicketSlaPausePeriodId]),
        CONSTRAINT [CK_TicketSlaPausePeriods_Order] CHECK ([ResumedAtUtc] IS NULL OR [ResumedAtUtc] >= [StartedAtUtc]),
        CONSTRAINT [FK_TicketSlaPausePeriods_TicketSlaInstances_TicketSlaInstanceId] FOREIGN KEY ([TicketSlaInstanceId]) REFERENCES [TicketSlaInstances] ([TicketSlaInstanceId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketSlaPausePeriods_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE INDEX [IX_PriorityDowngradeRequests_CurrentPriorityId] ON [PriorityDowngradeRequests] ([CurrentPriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE INDEX [IX_PriorityDowngradeRequests_DecidedByEmployeeId] ON [PriorityDowngradeRequests] ([DecidedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE INDEX [IX_PriorityDowngradeRequests_RequestedByEmployeeId] ON [PriorityDowngradeRequests] ([RequestedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE INDEX [IX_PriorityDowngradeRequests_RequestedPriorityId] ON [PriorityDowngradeRequests] ([RequestedPriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE INDEX [IX_PriorityDowngradeRequests_StatusExpiry] ON [PriorityDowngradeRequests] ([Status], [ExpiresAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_PriorityDowngradeRequests_OnePendingPerTicket] ON [PriorityDowngradeRequests] ([TicketId]) WHERE [Status] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    CREATE INDEX [IX_TicketSlaPausePeriods_Instance] ON [TicketSlaPausePeriods] ([TicketSlaInstanceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketSlaPausePeriods_OneOpenPerTicket] ON [TicketSlaPausePeriods] ([TicketId]) WHERE [ResumedAtUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261008184722_AddSlaPauseAndPriorityDowngrade'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261008184722_AddSlaPauseAndPriorityDowngrade', N'10.0.11');
END;

COMMIT;
GO

