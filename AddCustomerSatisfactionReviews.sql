BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE TABLE [TicketReviewInvitations] (
        [TicketReviewInvitationId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [ReopenCycle] int NOT NULL,
        [TokenHash] varbinary(32) NOT NULL,
        [IsTest] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketReviewInvitations] PRIMARY KEY ([TicketReviewInvitationId]),
        CONSTRAINT [FK_TicketReviewInvitations_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE TABLE [TicketReviews] (
        [TicketReviewId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [ReopenCycle] int NOT NULL,
        [TicketReviewInvitationId] bigint NOT NULL,
        [IsTest] bit NOT NULL,
        [Rating] tinyint NOT NULL,
        [Comment] nvarchar(1000) NULL,
        [SubmittedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketReviews] PRIMARY KEY ([TicketReviewId]),
        CONSTRAINT [CK_TicketReviews_Rating] CHECK ([Rating] BETWEEN 1 AND 5),
        CONSTRAINT [FK_TicketReviews_TicketReviewInvitations_TicketReviewInvitationId] FOREIGN KEY ([TicketReviewInvitationId]) REFERENCES [TicketReviewInvitations] ([TicketReviewInvitationId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketReviews_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE INDEX [IX_TicketReviewInvitations_TicketId_ReopenCycle] ON [TicketReviewInvitations] ([TicketId], [ReopenCycle]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TicketReviewInvitations_TokenHash] ON [TicketReviewInvitations] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE INDEX [IX_TicketReviews_IsTest_SubmittedAtUtc] ON [TicketReviews] ([IsTest], [SubmittedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE INDEX [IX_TicketReviews_TicketReviewInvitationId] ON [TicketReviews] ([TicketReviewInvitationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TicketReviews_TicketId_ReopenCycle_IsTest] ON [TicketReviews] ([TicketId], [ReopenCycle], [IsTest]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261022081031_AddCustomerSatisfactionReviews'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261022081031_AddCustomerSatisfactionReviews', N'10.0.11');
END;

COMMIT;
GO

