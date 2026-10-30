BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030073857_AddTicketCreatedBy'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CreatedByEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030073857_AddTicketCreatedBy'
)
BEGIN
    CREATE INDEX [IX_Tickets_CreatedByEmployeeId] ON [Tickets] ([CreatedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030073857_AddTicketCreatedBy'
)
BEGIN
    EXEC(N'UPDATE [Tickets]
    SET [CreatedByEmployeeId] = (
        SELECT a.[ActorEmployeeId] FROM [AuditEntries] AS a
        WHERE a.[AuditEntryId] = (
            SELECT MIN(a2.[AuditEntryId]) FROM [AuditEntries] AS a2
            WHERE a2.[EntityType] = ''Ticket'' AND a2.[Action] = ''Create'' AND a2.[ActorEmployeeId] IS NOT NULL
              AND a2.[EntityId] = CAST([Tickets].[TicketId] AS nvarchar(64))))
    WHERE [CreatedByEmployeeId] IS NULL;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261030073857_AddTicketCreatedBy'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261030073857_AddTicketCreatedBy', N'10.0.11');
END;

COMMIT;
GO

