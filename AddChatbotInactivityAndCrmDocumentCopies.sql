BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [AwaitingCustomerReplyReportedByEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [AwaitingCustomerReplySinceUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    ALTER TABLE [TicketInteractions] ADD [InactivityClosedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    CREATE TABLE [CrmDocumentDeliveryRequests] (
        [CrmDocumentDeliveryRequestId] bigint NOT NULL IDENTITY,
        [CallerEmployeeId] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(128) NOT NULL,
        [Fingerprint] nvarchar(64) NOT NULL,
        [VerificationSessionId] uniqueidentifier NOT NULL,
        [DocumentType] tinyint NOT NULL,
        [CrmRecordId] nvarchar(100) NOT NULL,
        [Channel] tinyint NOT NULL,
        [MaskedDestination] nvarchar(120) NULL,
        [Status] tinyint NOT NULL,
        [FailureCode] nvarchar(64) NULL,
        [AttemptCount] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        [SentAtUtc] datetime2 NULL,
        CONSTRAINT [PK_CrmDocumentDeliveryRequests] PRIMARY KEY ([CrmDocumentDeliveryRequestId]),
        CONSTRAINT [FK_CrmDocumentDeliveryRequests_AspNetUsers_CallerEmployeeId] FOREIGN KEY ([CallerEmployeeId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TicketInteractions_AwaitingCustomerReplySinceUtc] ON [TicketInteractions] ([AwaitingCustomerReplySinceUtc]) WHERE [AwaitingCustomerReplySinceUtc] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    CREATE INDEX [IX_CrmDocumentDeliveryRequests_SessionDocument] ON [CrmDocumentDeliveryRequests] ([VerificationSessionId], [DocumentType], [CrmRecordId], [Channel], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CrmDocumentDeliveryRequests_CallerKey] ON [CrmDocumentDeliveryRequests] ([CallerEmployeeId], [IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007093644_AddChatbotInactivityAndCrmDocumentCopies', N'10.0.11');
END;

COMMIT;
GO

