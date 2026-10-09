BEGIN TRANSACTION;
ALTER TABLE [CustomerOtpChallenges] ADD [Channel] tinyint NOT NULL DEFAULT CAST(1 AS tinyint);

ALTER TABLE [CustomerOtpChallenges] ADD [DeliveryState] tinyint NOT NULL DEFAULT CAST(0 AS tinyint);

ALTER TABLE [CustomerOtpChallenges] ADD [Language] nvarchar(2) NOT NULL DEFAULT N'en';

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261009102002_AddSmsChannelToCustomerOtp', N'10.0.11');

COMMIT;
GO

