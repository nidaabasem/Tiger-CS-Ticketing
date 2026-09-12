BEGIN TRANSACTION;
CREATE TABLE [Channels] (
    [ChannelId] tinyint NOT NULL IDENTITY,
    [Name] nvarchar(100) NOT NULL,
    [Code] nvarchar(50) NOT NULL,
    [RequiresPhone] bit NOT NULL,
    [IsGenesysEnabled] bit NOT NULL,
    [IsActive] bit NOT NULL,
    [DisplayOrder] int NOT NULL,
    CONSTRAINT [PK_Channels] PRIMARY KEY ([ChannelId])
);

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ChannelId', N'Name', N'Code', N'RequiresPhone', N'IsGenesysEnabled', N'IsActive', N'DisplayOrder') AND [object_id] = OBJECT_ID(N'[Channels]'))
    SET IDENTITY_INSERT [Channels] ON;
INSERT INTO [Channels] ([ChannelId], [Name], [Code], [RequiresPhone], [IsGenesysEnabled], [IsActive], [DisplayOrder])
VALUES (CAST(1 AS tinyint), N'Phone', N'PHONE', CAST(1 AS bit), CAST(1 AS bit), CAST(1 AS bit), 1),
(CAST(2 AS tinyint), N'App / Website (Legacy)', N'LEGACY_APP_OR_WEBSITE', CAST(1 AS bit), CAST(0 AS bit), CAST(0 AS bit), 101),
(CAST(3 AS tinyint), N'WhatsApp / Live Chat (Legacy)', N'LEGACY_WHATSAPP_OR_LIVE_CHAT', CAST(1 AS bit), CAST(1 AS bit), CAST(0 AS bit), 102),
(CAST(4 AS tinyint), N'Social Media Direct Message', N'SOCIAL_DM', CAST(1 AS bit), CAST(1 AS bit), CAST(1 AS bit), 4),
(CAST(5 AS tinyint), N'Walk in / Kiosk', N'WALK_IN_KIOSK', CAST(0 AS bit), CAST(0 AS bit), CAST(1 AS bit), 6),
(CAST(6 AS tinyint), N'WhatsApp', N'WHATSAPP', CAST(1 AS bit), CAST(1 AS bit), CAST(1 AS bit), 2),
(CAST(7 AS tinyint), N'Live Chat', N'LIVE_CHAT', CAST(1 AS bit), CAST(1 AS bit), CAST(1 AS bit), 3),
(CAST(8 AS tinyint), N'Website', N'WEBSITE', CAST(1 AS bit), CAST(0 AS bit), CAST(1 AS bit), 5),
(CAST(9 AS tinyint), N'Mobile App (Customer Portal)', N'MOBILE_APP', CAST(1 AS bit), CAST(0 AS bit), CAST(1 AS bit), 7),
(CAST(10 AS tinyint), N'Instagram', N'INSTAGRAM', CAST(1 AS bit), CAST(1 AS bit), CAST(1 AS bit), 8),
(CAST(11 AS tinyint), N'Facebook', N'FACEBOOK', CAST(1 AS bit), CAST(1 AS bit), CAST(1 AS bit), 9);
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'ChannelId', N'Name', N'Code', N'RequiresPhone', N'IsGenesysEnabled', N'IsActive', N'DisplayOrder') AND [object_id] = OBJECT_ID(N'[Channels]'))
    SET IDENTITY_INSERT [Channels] OFF;

CREATE INDEX [IX_TicketInteractions_ChannelId] ON [TicketInteractions] ([ChannelId]);

CREATE INDEX [IX_IntakeRecords_ChannelId] ON [IntakeRecords] ([ChannelId]);

CREATE UNIQUE INDEX [IX_Channels_Code] ON [Channels] ([Code]);

ALTER TABLE [IntakeRecords] ADD CONSTRAINT [FK_IntakeRecords_Channels_ChannelId] FOREIGN KEY ([ChannelId]) REFERENCES [Channels] ([ChannelId]) ON DELETE NO ACTION;

ALTER TABLE [TicketInteractions] ADD CONSTRAINT [FK_TicketInteractions_Channels_ChannelId] FOREIGN KEY ([ChannelId]) REFERENCES [Channels] ([ChannelId]) ON DELETE NO ACTION;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260909084839_AddChannels', N'10.0.11');

COMMIT;
GO

