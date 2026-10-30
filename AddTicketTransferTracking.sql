BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030062642_AddTicketTransferTracking'
)
BEGIN
    ALTER TABLE [TicketWorkflowEvents] ADD [FromDepartmentId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030062642_AddTicketTransferTracking'
)
BEGIN
    ALTER TABLE [TicketWorkflowEvents] ADD [ToDepartmentId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030062642_AddTicketTransferTracking'
)
BEGIN
    ALTER TABLE [TicketWorkflowEvents] ADD [ToEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030062642_AddTicketTransferTracking'
)
BEGIN
    ALTER TABLE [TicketWorkflowEvents] ADD [TransferReasonCode] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030062642_AddTicketTransferTracking'
)
BEGIN
    CREATE INDEX [IX_TicketWorkflowEvents_EventType_TransferReasonCode] ON [TicketWorkflowEvents] ([EventType], [TransferReasonCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030062642_AddTicketTransferTracking'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261030062642_AddTicketTransferTracking', N'10.0.11');
END;

COMMIT;
GO

