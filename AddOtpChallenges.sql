BEGIN TRANSACTION;
CREATE TABLE [OtpChallenges] (
    [OtpChallengeId] uniqueidentifier NOT NULL,
    [OwnerEmployeeId] uniqueidentifier NOT NULL,
    [CrmCustomerId] int NOT NULL,
    [CrmUnitId] int NOT NULL,
    [UnitNumber] nvarchar(64) NULL,
    [ProjectName] nvarchar(256) NULL,
    [Channel] nvarchar(16) NOT NULL,
    [Destination] nvarchar(32) NOT NULL,
    [Language] nvarchar(2) NOT NULL,
    [CodeHash] char(64) NOT NULL,
    [CreatedAtUtc] datetime2 NOT NULL,
    [ExpiresAtUtc] datetime2 NOT NULL,
    [LastSentAtUtc] datetime2 NOT NULL,
    [SendCount] int NOT NULL,
    [FailedAttempts] int NOT NULL,
    [Status] int NOT NULL,
    [DeliveryState] int NOT NULL,
    [VerificationSessionId] uniqueidentifier NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_OtpChallenges] PRIMARY KEY ([OtpChallengeId])
);

CREATE INDEX [IX_OtpChallenges_Customer_CreatedAt] ON [OtpChallenges] ([CrmCustomerId], [CreatedAtUtc]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261009090448_AddOtpChallenges', N'10.0.11');

COMMIT;
GO

