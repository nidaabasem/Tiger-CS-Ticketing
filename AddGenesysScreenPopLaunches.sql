BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928132618_AddGenesysScreenPopLaunches'
)
BEGIN
    CREATE TABLE [GenesysScreenPopLaunches] (
        [GenesysScreenPopLaunchId] bigint NOT NULL IDENTITY,
        [TokenHash] char(64) NOT NULL,
        [GenesysUserId] nvarchar(64) NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [TargetPath] nvarchar(512) NOT NULL,
        [ConversationId] nvarchar(128) NULL,
        [TicketId] bigint NULL,
        [IssuedByEmployeeId] uniqueidentifier NOT NULL,
        [IssuedAtUtc] datetime2 NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [RedeemedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_GenesysScreenPopLaunches] PRIMARY KEY ([GenesysScreenPopLaunchId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928132618_AddGenesysScreenPopLaunches'
)
BEGIN
    CREATE UNIQUE INDEX [UX_GenesysScreenPopLaunches_TokenHash] ON [GenesysScreenPopLaunches] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260928132618_AddGenesysScreenPopLaunches'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260928132618_AddGenesysScreenPopLaunches', N'10.0.11');
END;

COMMIT;
GO

