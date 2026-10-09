BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    ALTER TABLE [VerificationSessions] ADD [CrmBuyerCustomerId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    ALTER TABLE [VerificationSessions] ADD [CrmBuyerLeadId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    ALTER TABLE [VerificationSessions] ADD [ProofChallengeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    CREATE TABLE [CustomerOtpChallenges] (
        [CustomerOtpChallengeId] uniqueidentifier NOT NULL,
        [CallerEmployeeId] uniqueidentifier NOT NULL,
        [CrmCustomerId] int NOT NULL,
        [CrmLeadId] int NOT NULL,
        [UnitReferenceId] int NOT NULL,
        [ContactReferenceId] int NOT NULL,
        [MaskedDestination] nvarchar(120) NOT NULL,
        [Salt] varbinary(32) NOT NULL,
        [CodeHash] varbinary(32) NOT NULL,
        [Status] tinyint NOT NULL,
        [FailedAttempts] int NOT NULL,
        [SendCount] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [LastSentAtUtc] datetime2 NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [VerifiedAtUtc] datetime2 NULL,
        [VerificationSessionId] uniqueidentifier NULL,
        CONSTRAINT [PK_CustomerOtpChallenges] PRIMARY KEY ([CustomerOtpChallengeId]),
        CONSTRAINT [FK_CustomerOtpChallenges_AspNetUsers_CallerEmployeeId] FOREIGN KEY ([CallerEmployeeId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustomerOtpChallenges_ContactReferences_ContactReferenceId] FOREIGN KEY ([ContactReferenceId]) REFERENCES [ContactReferences] ([ContactReferenceId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustomerOtpChallenges_UnitReferences_UnitReferenceId] FOREIGN KEY ([UnitReferenceId]) REFERENCES [UnitReferences] ([UnitReferenceId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_VerificationSessions_ProofChallengeId] ON [VerificationSessions] ([ProofChallengeId]) WHERE [ProofChallengeId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    CREATE INDEX [IX_CustomerOtpChallenges_CallerLead] ON [CustomerOtpChallenges] ([CallerEmployeeId], [CrmCustomerId], [CrmLeadId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    CREATE INDEX [IX_CustomerOtpChallenges_ContactReferenceId] ON [CustomerOtpChallenges] ([ContactReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    CREATE INDEX [IX_CustomerOtpChallenges_Customer] ON [CustomerOtpChallenges] ([CrmCustomerId], [CreatedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    CREATE INDEX [IX_CustomerOtpChallenges_UnitReferenceId] ON [CustomerOtpChallenges] ([UnitReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007180009_AddCustomerOtpVerification'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007180009_AddCustomerOtpVerification', N'10.0.11');
END;

COMMIT;
GO

