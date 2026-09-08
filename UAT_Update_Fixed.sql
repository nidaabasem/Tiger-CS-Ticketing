IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] uniqueidentifier NOT NULL,
        [Description] nvarchar(500) NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] uniqueidentifier NOT NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [Departments] (
        [DepartmentId] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [Code] nvarchar(10) NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Departments] PRIMARY KEY ([DepartmentId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] uniqueidentifier NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] uniqueidentifier NOT NULL,
        [RoleId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] uniqueidentifier NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [Employees] (
        [EmployeeId] uniqueidentifier NOT NULL,
        [DisplayName] nvarchar(200) NOT NULL,
        [IsGeynessStaff] bit NOT NULL,
        [DeactivatedAtUtc] datetime2 NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Employees] PRIMARY KEY ([EmployeeId]),
        CONSTRAINT [FK_Employees_AspNetUsers_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE TABLE [UserDepartmentAssignments] (
        [UserDepartmentAssignmentId] int NOT NULL IDENTITY,
        [EmployeeId] uniqueidentifier NOT NULL,
        [DepartmentId] int NOT NULL,
        [IsPrimary] bit NOT NULL,
        [AssignedAtUtc] datetime2 NOT NULL,
        [AssignedByEmployeeId] uniqueidentifier NULL,
        CONSTRAINT [PK_UserDepartmentAssignments] PRIMARY KEY ([UserDepartmentAssignmentId]),
        CONSTRAINT [FK_UserDepartmentAssignments_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_UserDepartmentAssignments_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Departments_Code] ON [Departments] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Departments_Name] ON [Departments] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE INDEX [IX_UserDepartmentAssignments_DepartmentId] ON [UserDepartmentAssignments] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UserDepartmentAssignments_EmployeeId_DepartmentId] ON [UserDepartmentAssignments] ([EmployeeId], [DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_UserDepartmentAssignments_EmployeeId_PrimaryOnly] ON [UserDepartmentAssignments] ([EmployeeId]) WHERE [IsPrimary] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260819072347_InitialIdentityAndAccess'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260819072347_InitialIdentityAndAccess', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE TABLE [AuditEntries] (
        [AuditEntryId] bigint NOT NULL IDENTITY,
        [ActorEmployeeId] uniqueidentifier NULL,
        [Action] nvarchar(100) NOT NULL,
        [EntityType] nvarchar(100) NOT NULL,
        [EntityId] nvarchar(100) NULL,
        [BeforeValue] nvarchar(max) NULL,
        [AfterValue] nvarchar(max) NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_AuditEntries] PRIMARY KEY ([AuditEntryId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE TABLE [UnitReferences] (
        [UnitReferenceId] int NOT NULL IDENTITY,
        [CrmUnitId] nvarchar(64) NOT NULL,
        [UnitNumber] nvarchar(50) NOT NULL,
        [PropertyName] nvarchar(200) NULL,
        [TowerName] nvarchar(200) NULL,
        [UnitType] nvarchar(50) NULL,
        [LastSyncedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_UnitReferences] PRIMARY KEY ([UnitReferenceId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE TABLE [ContactReferences] (
        [ContactReferenceId] int NOT NULL IDENTITY,
        [CrmContactId] nvarchar(64) NOT NULL,
        [UnitReferenceId] int NOT NULL,
        [DisplayName] nvarchar(200) NULL,
        [ContactChannel] nvarchar(200) NULL,
        [ContactType] int NOT NULL,
        [AuthorizedRepresentativeOfContactReferenceId] int NULL,
        [LastSyncedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_ContactReferences] PRIMARY KEY ([ContactReferenceId]),
        CONSTRAINT [FK_ContactReferences_ContactReferences_AuthorizedRepresentativeOfContactReferenceId] FOREIGN KEY ([AuthorizedRepresentativeOfContactReferenceId]) REFERENCES [ContactReferences] ([ContactReferenceId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ContactReferences_UnitReferences_UnitReferenceId] FOREIGN KEY ([UnitReferenceId]) REFERENCES [UnitReferences] ([UnitReferenceId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE TABLE [VerificationSessions] (
        [VerificationSessionId] uniqueidentifier NOT NULL,
        [AgentEmployeeId] uniqueidentifier NOT NULL,
        [UnitReferenceId] int NOT NULL,
        [ContactReferenceId] int NOT NULL,
        [SnapshotUnitNumber] nvarchar(50) NULL,
        [SnapshotPropertyName] nvarchar(200) NULL,
        [SnapshotTowerName] nvarchar(200) NULL,
        [SnapshotUnitType] nvarchar(50) NULL,
        [SnapshotContactDisplayName] nvarchar(200) NULL,
        [SnapshotContactChannel] nvarchar(200) NULL,
        [Confirmed] bit NOT NULL,
        [VerificationMethod] int NULL,
        [Status] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [ConfirmedAtUtc] datetime2 NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [ConsumedAtUtc] datetime2 NULL,
        [ConsumedByTicketId] bigint NULL,
        [IdempotencyKey] nvarchar(300) NULL,
        CONSTRAINT [PK_VerificationSessions] PRIMARY KEY ([VerificationSessionId]),
        CONSTRAINT [FK_VerificationSessions_ContactReferences_ContactReferenceId] FOREIGN KEY ([ContactReferenceId]) REFERENCES [ContactReferences] ([ContactReferenceId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_VerificationSessions_UnitReferences_UnitReferenceId] FOREIGN KEY ([UnitReferenceId]) REFERENCES [UnitReferences] ([UnitReferenceId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE INDEX [IX_ContactReferences_AuthorizedRepresentativeOfContactReferenceId] ON [ContactReferences] ([AuthorizedRepresentativeOfContactReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ContactReferences_CrmContactId] ON [ContactReferences] ([CrmContactId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE INDEX [IX_ContactReferences_UnitReferenceId] ON [ContactReferences] ([UnitReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE UNIQUE INDEX [IX_UnitReferences_CrmUnitId] ON [UnitReferences] ([CrmUnitId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_VerificationSessions_AgentEmployeeId_IdempotencyKey] ON [VerificationSessions] ([AgentEmployeeId], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE INDEX [IX_VerificationSessions_ContactReferenceId] ON [VerificationSessions] ([ContactReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    CREATE INDEX [IX_VerificationSessions_UnitReferenceId] ON [VerificationSessions] ([UnitReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820072044_AddCustomerVerification'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260820072044_AddCustomerVerification', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE TABLE [Priorities] (
        [PriorityId] tinyint NOT NULL,
        [Name] nvarchar(20) NOT NULL,
        [DisplayOrder] tinyint NOT NULL,
        CONSTRAINT [PK_Priorities] PRIMARY KEY ([PriorityId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE TABLE [Categories] (
        [CategoryId] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [ParentCategoryId] int NULL,
        [DepartmentId] int NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_Categories] PRIMARY KEY ([CategoryId]),
        CONSTRAINT [FK_Categories_Categories_ParentCategoryId] FOREIGN KEY ([ParentCategoryId]) REFERENCES [Categories] ([CategoryId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Categories_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE TABLE [Tickets] (
        [TicketId] bigint NOT NULL IDENTITY,
        [TicketNumber] nvarchar(40) NOT NULL,
        [OriginatingDepartmentId] int NOT NULL,
        [CurrentDepartmentId] int NOT NULL,
        [CurrentOwnerEmployeeId] uniqueidentifier NULL,
        [UnitReferenceId] int NULL,
        [ContactReferenceId] int NULL,
        [CategoryId] int NOT NULL,
        [PriorityId] tinyint NOT NULL,
        [TicketStatus] tinyint NOT NULL,
        [VerificationStatus] tinyint NOT NULL,
        [EscalationLevel] tinyint NOT NULL,
        [SlaState] tinyint NOT NULL,
        [ResolutionOutcome] tinyint NULL,
        [DuplicateOfTicketId] bigint NULL,
        [RequestSummary] nvarchar(2000) NOT NULL,
        [FirstHumanResponseAtUtc] datetime2 NULL,
        [AcknowledgementSentAtUtc] datetime2 NULL,
        [ReopenCount] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Tickets] PRIMARY KEY ([TicketId]),
        CONSTRAINT [FK_Tickets_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([CategoryId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_ContactReferences_ContactReferenceId] FOREIGN KEY ([ContactReferenceId]) REFERENCES [ContactReferences] ([ContactReferenceId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_Departments_CurrentDepartmentId] FOREIGN KEY ([CurrentDepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_Departments_OriginatingDepartmentId] FOREIGN KEY ([OriginatingDepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_Employees_CurrentOwnerEmployeeId] FOREIGN KEY ([CurrentOwnerEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_Priorities_PriorityId] FOREIGN KEY ([PriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_Tickets_DuplicateOfTicketId] FOREIGN KEY ([DuplicateOfTicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Tickets_UnitReferences_UnitReferenceId] FOREIGN KEY ([UnitReferenceId]) REFERENCES [UnitReferences] ([UnitReferenceId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE TABLE [TicketRequesterSnapshots] (
        [TicketId] bigint NOT NULL,
        [SnapshotUnitNumber] nvarchar(50) NOT NULL,
        [SnapshotPropertyName] nvarchar(200) NULL,
        [SnapshotTowerName] nvarchar(200) NULL,
        [SnapshotUnitType] nvarchar(50) NULL,
        [SnapshotContactDisplayName] nvarchar(200) NULL,
        [SnapshotContactChannel] nvarchar(200) NULL,
        [CapturedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketRequesterSnapshots] PRIMARY KEY ([TicketId]),
        CONSTRAINT [FK_TicketRequesterSnapshots_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE TABLE [TicketStatusHistory] (
        [TicketStatusHistoryId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [Dimension] tinyint NOT NULL,
        [OldValue] tinyint NULL,
        [NewValue] tinyint NOT NULL,
        [ActorEmployeeId] uniqueidentifier NULL,
        [ActorIsSystem] bit NOT NULL,
        [Note] nvarchar(1000) NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketStatusHistory] PRIMARY KEY ([TicketStatusHistoryId]),
        CONSTRAINT [FK_TicketStatusHistory_Employees_ActorEmployeeId] FOREIGN KEY ([ActorEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketStatusHistory_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE TABLE [IntakeRecords] (
        [IntakeRecordId] bigint NOT NULL IDENTITY,
        [ChannelId] tinyint NOT NULL,
        [ReceivedAtUtc] datetime2 NOT NULL,
        [IsUnitRelated] bit NOT NULL,
        [RawUnitNumberEntered] nvarchar(50) NULL,
        [PriorityHint] tinyint NULL,
        [CrmVerificationStatus] tinyint NOT NULL,
        [CreatedByEmployeeId] uniqueidentifier NOT NULL,
        [LinkedTicketId] bigint NULL,
        CONSTRAINT [PK_IntakeRecords] PRIMARY KEY ([IntakeRecordId]),
        CONSTRAINT [FK_IntakeRecords_Employees_CreatedByEmployeeId] FOREIGN KEY ([CreatedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_IntakeRecords_Priorities_PriorityHint] FOREIGN KEY ([PriorityHint]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_IntakeRecords_Tickets_LinkedTicketId] FOREIGN KEY ([LinkedTicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Categories_DepartmentId] ON [Categories] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Categories_ParentCategoryId] ON [Categories] ([ParentCategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_IntakeRecords_CreatedByEmployeeId] ON [IntakeRecords] ([CreatedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_IntakeRecords_LinkedTicketId] ON [IntakeRecords] ([LinkedTicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_IntakeRecords_PriorityHint] ON [IntakeRecords] ([PriorityHint]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_TicketStatusHistory_ActorEmployeeId] ON [TicketStatusHistory] ([ActorEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_TicketStatusHistory_TicketId] ON [TicketStatusHistory] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_CategoryId] ON [Tickets] ([CategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_ContactReferenceId] ON [Tickets] ([ContactReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_CurrentDepartmentId] ON [Tickets] ([CurrentDepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_CurrentOwnerEmployeeId] ON [Tickets] ([CurrentOwnerEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_DuplicateOfTicketId] ON [Tickets] ([DuplicateOfTicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_OriginatingDepartmentId] ON [Tickets] ([OriginatingDepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_PriorityId] ON [Tickets] ([PriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Tickets_TicketNumber] ON [Tickets] ([TicketNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    CREATE INDEX [IX_Tickets_UnitReferenceId] ON [Tickets] ([UnitReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820140000_AddTicketing'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260820140000_AddTicketing', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    ALTER TABLE [Tickets] ADD [RowVersion] rowversion NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE TABLE [TicketAssignments] (
        [TicketAssignmentId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [AssignedEmployeeId] uniqueidentifier NOT NULL,
        [AssignedDepartmentId] int NOT NULL,
        [AssignedAtUtc] datetime2 NOT NULL,
        [AssigningActorEmployeeId] uniqueidentifier NULL,
        [IsCurrent] bit NOT NULL,
        CONSTRAINT [PK_TicketAssignments] PRIMARY KEY ([TicketAssignmentId]),
        CONSTRAINT [FK_TicketAssignments_Departments_AssignedDepartmentId] FOREIGN KEY ([AssignedDepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignments_Employees_AssignedEmployeeId] FOREIGN KEY ([AssignedEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignments_Employees_AssigningActorEmployeeId] FOREIGN KEY ([AssigningActorEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketAssignments_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE TABLE [TicketResolutions] (
        [TicketResolutionId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [ResolutionOutcome] tinyint NOT NULL,
        [ResolutionNote] nvarchar(4000) NOT NULL,
        [ReasonCode] tinyint NULL,
        [DuplicateOfTicketId] bigint NULL,
        [ResolvingEmployeeId] uniqueidentifier NOT NULL,
        [ResolvedAtUtc] datetime2 NOT NULL,
        [IsCurrent] bit NOT NULL,
        CONSTRAINT [PK_TicketResolutions] PRIMARY KEY ([TicketResolutionId]),
        CONSTRAINT [FK_TicketResolutions_Employees_ResolvingEmployeeId] FOREIGN KEY ([ResolvingEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketResolutions_Tickets_DuplicateOfTicketId] FOREIGN KEY ([DuplicateOfTicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketResolutions_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE TABLE [TicketNotes] (
        [TicketNoteId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [NoteText] nvarchar(2000) NOT NULL,
        [AuthorEmployeeId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketNotes] PRIMARY KEY ([TicketNoteId]),
        CONSTRAINT [FK_TicketNotes_Employees_AuthorEmployeeId] FOREIGN KEY ([AuthorEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketNotes_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketAssignments_AssignedDepartmentId] ON [TicketAssignments] ([AssignedDepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketAssignments_AssignedEmployeeId] ON [TicketAssignments] ([AssignedEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketAssignments_AssigningActorEmployeeId] ON [TicketAssignments] ([AssigningActorEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_TicketAssignments_TicketId] ON [TicketAssignments] ([TicketId]) WHERE [IsCurrent] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketNotes_AuthorEmployeeId] ON [TicketNotes] ([AuthorEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketNotes_TicketId] ON [TicketNotes] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketResolutions_DuplicateOfTicketId] ON [TicketResolutions] ([DuplicateOfTicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    CREATE INDEX [IX_TicketResolutions_ResolvingEmployeeId] ON [TicketResolutions] ([ResolvingEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_TicketResolutions_TicketId] ON [TicketResolutions] ([TicketId]) WHERE [IsCurrent] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821090000_AddTicketOperations'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260821090000_AddTicketOperations', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [BusinessCalendars] (
        [BusinessCalendarId] int NOT NULL IDENTITY,
        [Name] nvarchar(100) NOT NULL,
        [BusinessDayStartLocal] time NOT NULL,
        [BusinessDayEndLocal] time NOT NULL,
        [TimeZone] nvarchar(50) NOT NULL,
        [EffectiveFromUtc] datetime2 NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_BusinessCalendars] PRIMARY KEY ([BusinessCalendarId]),
        CONSTRAINT [CK_BusinessCalendars_WindowOrder] CHECK ([BusinessDayEndLocal] > [BusinessDayStartLocal])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [IdempotencyRecords] (
        [IdempotencyRecordId] bigint NOT NULL IDENTITY,
        [IdempotencyKey] nvarchar(300) NOT NULL,
        [Scope] nvarchar(50) NOT NULL,
        [FirstSeenAtUtc] datetime2 NOT NULL,
        [LastSeenAtUtc] datetime2 NOT NULL,
        [ResultReference] nvarchar(200) NULL,
        [ExpiresAtUtc] datetime2 NULL,
        CONSTRAINT [PK_IdempotencyRecords] PRIMARY KEY ([IdempotencyRecordId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [SlaPolicies] (
        [PriorityId] tinyint NOT NULL,
        [FirstResponseTargetMinutes] int NOT NULL,
        [ResolutionTargetMinutes] int NOT NULL,
        [ClockBasis] tinyint NOT NULL,
        [WarningThresholdPercent] decimal(5,2) NOT NULL,
        [Level2ToGmWindowValue] int NOT NULL,
        [Level2ToGmWindowUnit] tinyint NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_SlaPolicies] PRIMARY KEY ([PriorityId]),
        CONSTRAINT [CK_SlaPolicies_PositiveTargets] CHECK ([FirstResponseTargetMinutes] > 0 AND [ResolutionTargetMinutes] > 0),
        CONSTRAINT [FK_SlaPolicies_Priorities_PriorityId] FOREIGN KEY ([PriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [TicketEscalations] (
        [TicketEscalationId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [Level] tinyint NOT NULL,
        [TriggerType] tinyint NOT NULL,
        [NotifiedRoles] nvarchar(200) NULL,
        [RaisedAtUtc] datetime2 NOT NULL,
        [RespondedAtUtc] datetime2 NULL,
        [RespondingEmployeeId] uniqueidentifier NULL,
        CONSTRAINT [PK_TicketEscalations] PRIMARY KEY ([TicketEscalationId]),
        CONSTRAINT [CK_TicketEscalations_Level4IsManualOnly] CHECK (([Level] = 4 AND [TriggerType] = 4) OR ([Level] <> 4 AND [TriggerType] <> 4)),
        CONSTRAINT [CK_TicketEscalations_LevelRange] CHECK ([Level] BETWEEN 1 AND 4),
        CONSTRAINT [FK_TicketEscalations_Employees_RespondingEmployeeId] FOREIGN KEY ([RespondingEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketEscalations_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [TicketSlaInstances] (
        [TicketSlaInstanceId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [PriorityId] tinyint NOT NULL,
        [PeriodStartAtUtc] datetime2 NOT NULL,
        [PeriodEndAtUtc] datetime2 NULL,
        [FirstResponseDueAtUtc] datetime2 NOT NULL,
        [ResolutionDueAtUtc] datetime2 NOT NULL,
        [FirstResponseBreached] bit NOT NULL,
        [ResolutionBreached] bit NOT NULL,
        [ChangeReason] tinyint NOT NULL,
        [ApprovedByEmployeeId] uniqueidentifier NULL,
        CONSTRAINT [PK_TicketSlaInstances] PRIMARY KEY ([TicketSlaInstanceId]),
        CONSTRAINT [CK_TicketSlaInstances_DowngradeRequiresApprover] CHECK ([ChangeReason] <> 3 OR [ApprovedByEmployeeId] IS NOT NULL),
        CONSTRAINT [CK_TicketSlaInstances_PeriodOrder] CHECK ([PeriodEndAtUtc] IS NULL OR [PeriodEndAtUtc] >= [PeriodStartAtUtc]),
        CONSTRAINT [FK_TicketSlaInstances_Employees_ApprovedByEmployeeId] FOREIGN KEY ([ApprovedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketSlaInstances_Priorities_PriorityId] FOREIGN KEY ([PriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketSlaInstances_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [BusinessCalendarWorkingDays] (
        [BusinessCalendarWorkingDayId] int NOT NULL IDENTITY,
        [BusinessCalendarId] int NOT NULL,
        [DayOfWeek] tinyint NOT NULL,
        [IsWorkingDay] bit NOT NULL,
        CONSTRAINT [PK_BusinessCalendarWorkingDays] PRIMARY KEY ([BusinessCalendarWorkingDayId]),
        CONSTRAINT [CK_BusinessCalendarWorkingDays_DayOfWeekRange] CHECK ([DayOfWeek] BETWEEN 0 AND 6),
        CONSTRAINT [FK_BusinessCalendarWorkingDays_BusinessCalendars_BusinessCalendarId] FOREIGN KEY ([BusinessCalendarId]) REFERENCES [BusinessCalendars] ([BusinessCalendarId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE TABLE [Holidays] (
        [HolidayId] int NOT NULL IDENTITY,
        [BusinessCalendarId] int NOT NULL,
        [HolidayDate] date NOT NULL,
        [Description] nvarchar(200) NULL,
        [EnteredByEmployeeId] uniqueidentifier NOT NULL,
        [ConfirmedByEmployeeId] uniqueidentifier NULL,
        [ConfirmedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_Holidays] PRIMARY KEY ([HolidayId]),
        CONSTRAINT [FK_Holidays_BusinessCalendars_BusinessCalendarId] FOREIGN KEY ([BusinessCalendarId]) REFERENCES [BusinessCalendars] ([BusinessCalendarId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Holidays_Employees_ConfirmedByEmployeeId] FOREIGN KEY ([ConfirmedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Holidays_Employees_EnteredByEmployeeId] FOREIGN KEY ([EnteredByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_BusinessCalendarWorkingDays_BusinessCalendarId_DayOfWeek] ON [BusinessCalendarWorkingDays] ([BusinessCalendarId], [DayOfWeek]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Holidays_BusinessCalendarId_HolidayDate] ON [Holidays] ([BusinessCalendarId], [HolidayDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_Holidays_ConfirmedByEmployeeId] ON [Holidays] ([ConfirmedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_Holidays_EnteredByEmployeeId] ON [Holidays] ([EnteredByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE UNIQUE INDEX [UX_IdempotencyRecords_IdempotencyKey] ON [IdempotencyRecords] ([IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_TicketEscalations_RespondingEmployeeId] ON [TicketEscalations] ([RespondingEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_TicketEscalations_TicketRaisedAt] ON [TicketEscalations] ([TicketId], [RaisedAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketEscalations_OneAutoBreachPerTicket] ON [TicketEscalations] ([TicketId]) WHERE [TriggerType] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_TicketSlaInstances_ApprovedByEmployeeId] ON [TicketSlaInstances] ([ApprovedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_TicketSlaInstances_FirstResponseSweep] ON [TicketSlaInstances] ([PeriodEndAtUtc], [FirstResponseBreached], [FirstResponseDueAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_TicketSlaInstances_PriorityId] ON [TicketSlaInstances] ([PriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    CREATE INDEX [IX_TicketSlaInstances_ResolutionSweep] ON [TicketSlaInstances] ([PeriodEndAtUtc], [ResolutionBreached], [ResolutionDueAtUtc]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketSlaInstances_CurrentPeriodPerTicket] ON [TicketSlaInstances] ([TicketId]) WHERE [PeriodEndAtUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821130000_AddSlaAndEscalation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260821130000_AddSlaAndEscalation', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    CREATE TABLE [OutboxMessages] (
        [OutboxMessageId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(200) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        [IdempotencyRecordId] bigint NOT NULL,
        [Status] tinyint NOT NULL,
        [Attempts] int NOT NULL,
        [LastError] nvarchar(2000) NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [ProcessedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_OutboxMessages] PRIMARY KEY ([OutboxMessageId]),
        CONSTRAINT [FK_OutboxMessages_IdempotencyRecords_IdempotencyRecordId] FOREIGN KEY ([IdempotencyRecordId]) REFERENCES [IdempotencyRecords] ([IdempotencyRecordId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    CREATE TABLE [Notifications] (
        [NotificationId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NULL,
        [NotificationType] tinyint NOT NULL,
        [RecipientEmployeeId] uniqueidentifier NULL,
        [RecipientAddress] nvarchar(320) NULL,
        [Channel] tinyint NOT NULL,
        [DeliveryStatus] tinyint NOT NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        [OutboxMessageId] uniqueidentifier NULL,
        [RetryCount] int NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([NotificationId]),
        CONSTRAINT [FK_Notifications_OutboxMessages_OutboxMessageId] FOREIGN KEY ([OutboxMessageId]) REFERENCES [OutboxMessages] ([OutboxMessageId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Notifications_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    CREATE INDEX [IX_Notifications_DeliveryStatus] ON [Notifications] ([DeliveryStatus]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    CREATE INDEX [IX_Notifications_TicketId] ON [Notifications] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Notifications_OutboxMessageId_NotificationType] ON [Notifications] ([OutboxMessageId], [NotificationType]) WHERE [OutboxMessageId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_CorrelationId] ON [OutboxMessages] ([CorrelationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessages_DeadLettered] ON [OutboxMessages] ([Status]) WHERE [Status] = 3');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    CREATE INDEX [IX_OutboxMessages_IdempotencyRecordId] ON [OutboxMessages] ([IdempotencyRecordId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessages_Status_OccurredAtUtc] ON [OutboxMessages] ([Status], [OccurredAtUtc]) WHERE [Status] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260822090000_AddNotificationsAndOutbox'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260822090000_AddNotificationsAndOutbox', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825053743_AddIntakeRecordPhoneNumber'
)
BEGIN
    ALTER TABLE [IntakeRecords] ADD [PhoneNumber] nvarchar(30) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825053743_AddIntakeRecordPhoneNumber'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260825053743_AddIntakeRecordPhoneNumber', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825061341_AddDepartmentCustomerLookupSource'
)
BEGIN
    ALTER TABLE [IntakeRecords] ADD [DepartmentId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825061341_AddDepartmentCustomerLookupSource'
)
BEGIN
    CREATE TABLE [DepartmentCustomerLookupSources] (
        [DepartmentCustomerLookupSourceId] int NOT NULL IDENTITY,
        [DepartmentId] int NOT NULL,
        [Source] tinyint NOT NULL,
        CONSTRAINT [PK_DepartmentCustomerLookupSources] PRIMARY KEY ([DepartmentCustomerLookupSourceId]),
        CONSTRAINT [FK_DepartmentCustomerLookupSources_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825061341_AddDepartmentCustomerLookupSource'
)
BEGIN
    CREATE INDEX [IX_IntakeRecords_DepartmentId] ON [IntakeRecords] ([DepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825061341_AddDepartmentCustomerLookupSource'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DepartmentCustomerLookupSources_DepartmentId_Source] ON [DepartmentCustomerLookupSources] ([DepartmentId], [Source]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825061341_AddDepartmentCustomerLookupSource'
)
BEGIN
    ALTER TABLE [IntakeRecords] ADD CONSTRAINT [FK_IntakeRecords_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260825061341_AddDepartmentCustomerLookupSource'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260825061341_AddDepartmentCustomerLookupSource', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerCustomerId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerCustomerName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerLeadId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerProjectId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerProjectName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerUnitId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CrmBuyerUnitNumber] nvarchar(50) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ManualProjectName] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ManualUnitNumber] nvarchar(50) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260827090146_AddCrmBuyerLookupToTickets'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260827090146_AddCrmBuyerLookupToTickets', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260901103309_AddExternalCustomerVerificationToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [CustomerVerificationSource] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260901103309_AddExternalCustomerVerificationToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ExternalCustomerId] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260901103309_AddExternalCustomerVerificationToTickets'
)
BEGIN
    ALTER TABLE [Tickets] ADD [ExternalUnitId] nvarchar(64) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260901103309_AddExternalCustomerVerificationToTickets'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Tickets_CustomerVerificationSource_ExternalCustomerId] ON [Tickets] ([CustomerVerificationSource], [ExternalCustomerId]) WHERE [ExternalCustomerId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260901103309_AddExternalCustomerVerificationToTickets'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260901103309_AddExternalCustomerVerificationToTickets', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE TABLE [DepartmentWorkflowSettings] (
        [DepartmentId] int NOT NULL,
        [AllowAssignment] bit NOT NULL,
        [AllowInternalReassignment] bit NOT NULL,
        [AllowTransferToOtherDepartments] bit NOT NULL,
        [HeadRoleName] nvarchar(64) NOT NULL,
        CONSTRAINT [PK_DepartmentWorkflowSettings] PRIMARY KEY ([DepartmentId]),
        CONSTRAINT [FK_DepartmentWorkflowSettings_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE TABLE [WorkflowTemplates] (
        [WorkflowTemplateId] int NOT NULL IDENTITY,
        [Code] nvarchar(32) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NULL,
        [AllowsPendingCustomer] bit NOT NULL,
        [AllowsPendingInternal] bit NOT NULL,
        [RequiresApproval] bit NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_WorkflowTemplates] PRIMARY KEY ([WorkflowTemplateId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE TABLE [RequestTypes] (
        [RequestTypeId] int NOT NULL IDENTITY,
        [DepartmentId] int NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [WorkflowTemplateId] int NOT NULL,
        [DefaultPriorityId] tinyint NOT NULL,
        [AllowAgentPriorityChange] bit NOT NULL,
        [AllowPendingCustomer] bit NOT NULL,
        [AllowPendingInternal] bit NOT NULL,
        [AllowReopen] bit NOT NULL,
        [RequiredFieldsJson] nvarchar(2000) NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_RequestTypes] PRIMARY KEY ([RequestTypeId]),
        CONSTRAINT [FK_RequestTypes_Departments_DepartmentId] FOREIGN KEY ([DepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypes_Priorities_DefaultPriorityId] FOREIGN KEY ([DefaultPriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypes_WorkflowTemplates_WorkflowTemplateId] FOREIGN KEY ([WorkflowTemplateId]) REFERENCES [WorkflowTemplates] ([WorkflowTemplateId]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE TABLE [WorkflowTemplateSteps] (
        [WorkflowTemplateStepId] int NOT NULL IDENTITY,
        [WorkflowTemplateId] int NOT NULL,
        [Sequence] tinyint NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Kind] tinyint NOT NULL,
        [IsOptional] bit NOT NULL,
        CONSTRAINT [PK_WorkflowTemplateSteps] PRIMARY KEY ([WorkflowTemplateStepId]),
        CONSTRAINT [FK_WorkflowTemplateSteps_WorkflowTemplates_WorkflowTemplateId] FOREIGN KEY ([WorkflowTemplateId]) REFERENCES [WorkflowTemplates] ([WorkflowTemplateId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE TABLE [RequestTypeSlaPolicies] (
        [RequestTypeSlaPolicyId] int NOT NULL IDENTITY,
        [RequestTypeId] int NOT NULL,
        [PriorityId] tinyint NOT NULL,
        [Trigger] tinyint NOT NULL,
        [Unit] tinyint NOT NULL,
        [FirstResponseTargetValue] int NULL,
        [FirstResponseMaximumValue] int NULL,
        [ResolutionTargetValue] int NULL,
        [ResolutionMaximumValue] int NULL,
        [IsImmediate] bit NOT NULL,
        [ClockBasis] tinyint NULL,
        [PausesOnPendingCustomer] bit NULL,
        [PausesOnPendingInternal] bit NULL,
        [WarningThresholdPercent] decimal(5,2) NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_RequestTypeSlaPolicies] PRIMARY KEY ([RequestTypeSlaPolicyId]),
        CONSTRAINT [FK_RequestTypeSlaPolicies_Priorities_PriorityId] FOREIGN KEY ([PriorityId]) REFERENCES [Priorities] ([PriorityId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypeSlaPolicies_RequestTypes_RequestTypeId] FOREIGN KEY ([RequestTypeId]) REFERENCES [RequestTypes] ([RequestTypeId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE INDEX [IX_RequestTypes_DefaultPriorityId] ON [RequestTypes] ([DefaultPriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RequestTypes_DepartmentId_Name] ON [RequestTypes] ([DepartmentId], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE INDEX [IX_RequestTypes_WorkflowTemplateId] ON [RequestTypes] ([WorkflowTemplateId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE INDEX [IX_RequestTypeSlaPolicies_PriorityId] ON [RequestTypeSlaPolicies] ([PriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RequestTypeSlaPolicies_RequestTypeId_PriorityId] ON [RequestTypeSlaPolicies] ([RequestTypeId], [PriorityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkflowTemplates_Code] ON [WorkflowTemplates] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence] ON [WorkflowTemplateSteps] ([WorkflowTemplateId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260903164723_AddWorkflowConfiguration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260903164723_AddWorkflowConfiguration', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    ALTER TABLE [Tickets] ADD [RequestTypeId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    ALTER TABLE [DepartmentWorkflowSettings] ADD [SupervisorRoleName] nvarchar(64) NOT NULL DEFAULT N'Department Head';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE TABLE [RequestTypeAssignmentRules] (
        [RequestTypeAssignmentRuleId] int NOT NULL IDENTITY,
        [RequestTypeId] int NOT NULL,
        [Mode] tinyint NOT NULL,
        [PrimaryEmployeeId] uniqueidentifier NULL,
        [TeamName] nvarchar(100) NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_RequestTypeAssignmentRules] PRIMARY KEY ([RequestTypeAssignmentRuleId]),
        CONSTRAINT [FK_RequestTypeAssignmentRules_Employees_PrimaryEmployeeId] FOREIGN KEY ([PrimaryEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypeAssignmentRules_RequestTypes_RequestTypeId] FOREIGN KEY ([RequestTypeId]) REFERENCES [RequestTypes] ([RequestTypeId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE TABLE [TicketInteractionContexts] (
        [TicketId] bigint NOT NULL,
        [Source] tinyint NOT NULL,
        [ChannelId] tinyint NOT NULL,
        [CustomerPhone] nvarchar(32) NOT NULL,
        [CalledNumber] nvarchar(32) NULL,
        [GenesysConversationId] nvarchar(64) NULL,
        [GenesysQueueId] nvarchar(64) NULL,
        [GenesysQueueName] nvarchar(200) NULL,
        [GenesysAgentId] nvarchar(64) NULL,
        [GenesysAgentName] nvarchar(200) NULL,
        [InteractionStartedAtUtc] datetime2 NULL,
        [Direction] nvarchar(32) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketInteractionContexts] PRIMARY KEY ([TicketId]),
        CONSTRAINT [FK_TicketInteractionContexts_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE TABLE [TicketPendingRecords] (
        [TicketPendingRecordId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [Kind] tinyint NOT NULL,
        [Reason] nvarchar(500) NOT NULL,
        [PreviousStatus] tinyint NOT NULL,
        [StartedAtUtc] datetime2 NOT NULL,
        [StartedByEmployeeId] uniqueidentifier NOT NULL,
        [ResumedAtUtc] datetime2 NULL,
        [ResumedByEmployeeId] uniqueidentifier NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_TicketPendingRecords] PRIMARY KEY ([TicketPendingRecordId]),
        CONSTRAINT [FK_TicketPendingRecords_Employees_ResumedByEmployeeId] FOREIGN KEY ([ResumedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketPendingRecords_Employees_StartedByEmployeeId] FOREIGN KEY ([StartedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketPendingRecords_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE TABLE [RequestTypeAssignmentRuleMembers] (
        [RequestTypeAssignmentRuleMemberId] int NOT NULL IDENTITY,
        [RequestTypeAssignmentRuleId] int NOT NULL,
        [EmployeeId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_RequestTypeAssignmentRuleMembers] PRIMARY KEY ([RequestTypeAssignmentRuleMemberId]),
        CONSTRAINT [FK_RequestTypeAssignmentRuleMembers_Employees_EmployeeId] FOREIGN KEY ([EmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypeAssignmentRuleMembers_RequestTypeAssignmentRules_RequestTypeAssignmentRuleId] FOREIGN KEY ([RequestTypeAssignmentRuleId]) REFERENCES [RequestTypeAssignmentRules] ([RequestTypeAssignmentRuleId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE INDEX [IX_Tickets_RequestTypeId] ON [Tickets] ([RequestTypeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE INDEX [IX_RequestTypeAssignmentRuleMembers_EmployeeId] ON [RequestTypeAssignmentRuleMembers] ([EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RequestTypeAssignmentRuleMembers_RequestTypeAssignmentRuleId_EmployeeId] ON [RequestTypeAssignmentRuleMembers] ([RequestTypeAssignmentRuleId], [EmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE INDEX [IX_RequestTypeAssignmentRules_PrimaryEmployeeId] ON [RequestTypeAssignmentRules] ([PrimaryEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RequestTypeAssignmentRules_RequestTypeId] ON [RequestTypeAssignmentRules] ([RequestTypeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TicketInteractionContexts_GenesysConversationId] ON [TicketInteractionContexts] ([GenesysConversationId]) WHERE [GenesysConversationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE INDEX [IX_TicketPendingRecords_ResumedByEmployeeId] ON [TicketPendingRecords] ([ResumedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    CREATE INDEX [IX_TicketPendingRecords_StartedByEmployeeId] ON [TicketPendingRecords] ([StartedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketPendingRecords_OpenPerTicket] ON [TicketPendingRecords] ([TicketId]) WHERE [ResumedAtUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    ALTER TABLE [Tickets] ADD CONSTRAINT [FK_Tickets_RequestTypes_RequestTypeId] FOREIGN KEY ([RequestTypeId]) REFERENCES [RequestTypes] ([RequestTypeId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904075902_AddWorkflowAutomation'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260904075902_AddWorkflowAutomation', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    CREATE TABLE [TicketInteractions] (
        [TicketInteractionId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [IsOriginatingInteraction] bit NOT NULL,
        [Source] tinyint NOT NULL,
        [ChannelId] tinyint NOT NULL,
        [CustomerPhone] nvarchar(32) NOT NULL,
        [CalledNumber] nvarchar(32) NULL,
        [GenesysConversationId] nvarchar(64) NULL,
        [GenesysQueueId] nvarchar(64) NULL,
        [GenesysQueueName] nvarchar(200) NULL,
        [GenesysAgentId] nvarchar(64) NULL,
        [GenesysAgentName] nvarchar(200) NULL,
        [InteractionStartedAtUtc] datetime2 NULL,
        [Direction] nvarchar(32) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_TicketInteractions] PRIMARY KEY ([TicketInteractionId]),
        CONSTRAINT [FK_TicketInteractions_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_TicketInteractions_GenesysConversationId] ON [TicketInteractions] ([GenesysConversationId]) WHERE [GenesysConversationId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    CREATE INDEX [IX_TicketInteractions_TicketId] ON [TicketInteractions] ([TicketId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketInteractions_OneOriginatingPerTicket] ON [TicketInteractions] ([TicketId]) WHERE [IsOriginatingInteraction] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    INSERT INTO [TicketInteractions]
        ([TicketId], [IsOriginatingInteraction], [Source], [ChannelId], [CustomerPhone], [CalledNumber],
         [GenesysConversationId], [GenesysQueueId], [GenesysQueueName], [GenesysAgentId], [GenesysAgentName],
         [InteractionStartedAtUtc], [Direction], [CreatedAtUtc])
    SELECT
        [TicketId], 1, [Source], [ChannelId], [CustomerPhone], [CalledNumber],
        [GenesysConversationId], [GenesysQueueId], [GenesysQueueName], [GenesysAgentId], [GenesysAgentName],
        [InteractionStartedAtUtc], [Direction], [CreatedAtUtc]
    FROM [TicketInteractionContexts];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    DROP TABLE [TicketInteractionContexts];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904082241_ReplaceInteractionContextWithTicketInteractions'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260904082241_ReplaceInteractionContextWithTicketInteractions', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE TABLE [RequestTypeApprovalRequirements] (
        [RequestTypeApprovalRequirementId] int NOT NULL IDENTITY,
        [RequestTypeId] int NOT NULL,
        [ApprovalType] tinyint NOT NULL,
        [TargetKind] tinyint NOT NULL,
        [TargetDepartmentId] int NULL,
        [TargetRoleName] nvarchar(64) NULL,
        [TargetEmployeeId] uniqueidentifier NULL,
        [BlocksWorkUntilApproved] bit NOT NULL,
        [IsActive] bit NOT NULL,
        CONSTRAINT [PK_RequestTypeApprovalRequirements] PRIMARY KEY ([RequestTypeApprovalRequirementId]),
        CONSTRAINT [FK_RequestTypeApprovalRequirements_Departments_TargetDepartmentId] FOREIGN KEY ([TargetDepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypeApprovalRequirements_Employees_TargetEmployeeId] FOREIGN KEY ([TargetEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_RequestTypeApprovalRequirements_RequestTypes_RequestTypeId] FOREIGN KEY ([RequestTypeId]) REFERENCES [RequestTypes] ([RequestTypeId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE TABLE [TicketApprovals] (
        [TicketApprovalId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [ApprovalType] tinyint NOT NULL,
        [Status] tinyint NOT NULL,
        [TargetKind] tinyint NOT NULL,
        [TargetDepartmentId] int NULL,
        [TargetRoleName] nvarchar(64) NULL,
        [TargetEmployeeId] uniqueidentifier NULL,
        [RequestedByEmployeeId] uniqueidentifier NOT NULL,
        [RequestedAtUtc] datetime2 NOT NULL,
        [RequestComment] nvarchar(1000) NULL,
        [DecidedByEmployeeId] uniqueidentifier NULL,
        [DecisionAtUtc] datetime2 NULL,
        [DecisionComment] nvarchar(1000) NULL,
        [IsCurrent] bit NOT NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_TicketApprovals] PRIMARY KEY ([TicketApprovalId]),
        CONSTRAINT [FK_TicketApprovals_Departments_TargetDepartmentId] FOREIGN KEY ([TargetDepartmentId]) REFERENCES [Departments] ([DepartmentId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketApprovals_Employees_DecidedByEmployeeId] FOREIGN KEY ([DecidedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketApprovals_Employees_RequestedByEmployeeId] FOREIGN KEY ([RequestedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketApprovals_Employees_TargetEmployeeId] FOREIGN KEY ([TargetEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketApprovals_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE TABLE [TicketWorkflowEvents] (
        [TicketWorkflowEventId] bigint NOT NULL IDENTITY,
        [TicketId] bigint NOT NULL,
        [EventType] tinyint NOT NULL,
        [OccurredAtUtc] datetime2 NOT NULL,
        [ActorEmployeeId] uniqueidentifier NULL,
        [TicketApprovalId] bigint NULL,
        [Note] nvarchar(500) NULL,
        [CorrelationId] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_TicketWorkflowEvents] PRIMARY KEY ([TicketWorkflowEventId]),
        CONSTRAINT [FK_TicketWorkflowEvents_Employees_ActorEmployeeId] FOREIGN KEY ([ActorEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketWorkflowEvents_TicketApprovals_TicketApprovalId] FOREIGN KEY ([TicketApprovalId]) REFERENCES [TicketApprovals] ([TicketApprovalId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_TicketWorkflowEvents_Tickets_TicketId] FOREIGN KEY ([TicketId]) REFERENCES [Tickets] ([TicketId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RequestTypeApprovalRequirements_RequestTypeId_ApprovalType] ON [RequestTypeApprovalRequirements] ([RequestTypeId], [ApprovalType]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_RequestTypeApprovalRequirements_TargetDepartmentId] ON [RequestTypeApprovalRequirements] ([TargetDepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_RequestTypeApprovalRequirements_TargetEmployeeId] ON [RequestTypeApprovalRequirements] ([TargetEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketApprovals_DecidedByEmployeeId] ON [TicketApprovals] ([DecidedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketApprovals_RequestedByEmployeeId] ON [TicketApprovals] ([RequestedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketApprovals_TargetDepartmentId] ON [TicketApprovals] ([TargetDepartmentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketApprovals_TargetEmployeeId] ON [TicketApprovals] ([TargetEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketApprovals_OneCurrentPerType] ON [TicketApprovals] ([TicketId], [ApprovalType]) WHERE [IsCurrent] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TicketApprovals_OnePendingPerType] ON [TicketApprovals] ([TicketId], [ApprovalType]) WHERE [Status] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketWorkflowEvents_ActorEmployeeId] ON [TicketWorkflowEvents] ([ActorEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketWorkflowEvents_TicketApprovalId] ON [TicketWorkflowEvents] ([TicketApprovalId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    CREATE INDEX [IX_TicketWorkflowEvents_TicketId_EventType] ON [TicketWorkflowEvents] ([TicketId], [EventType]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260904091143_AddApprovalWorkflow'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260904091143_AddApprovalWorkflow', N'10.0.11');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE TABLE [Workflows] (
        [WorkflowId] int NOT NULL IDENTITY,
        [Code] nvarchar(24) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [Description] nvarchar(500) NULL,
        [IsActive] bit NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_Workflows] PRIMARY KEY ([WorkflowId])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Workflows_Code] ON [Workflows] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    INSERT INTO [Workflows] ([Code], [Name], [Description], [IsActive], [CreatedAtUtc])
    SELECT [Code], [Name], [Description], [IsActive], SYSUTCDATETIME()
    FROM [WorkflowTemplates];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [WorkflowId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [VersionNumber] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [Status] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [CreatedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [CreatedByEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [PublishedAtUtc] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD [PublishedByEmployeeId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    UPDATE t
    SET t.[WorkflowId] = w.[WorkflowId],
        t.[VersionNumber] = 1,
        t.[Status] = 2,
        t.[CreatedAtUtc] = SYSUTCDATETIME(),
        t.[PublishedAtUtc] = SYSUTCDATETIME()
    FROM [WorkflowTemplates] t
    INNER JOIN [Workflows] w ON w.[Code] = t.[Code];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'WorkflowId');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [WorkflowTemplates] ALTER COLUMN [WorkflowId] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'VersionNumber');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [WorkflowTemplates] ALTER COLUMN [VersionNumber] int NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    DECLARE @var2 nvarchar(max);
    SELECT @var2 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'Status');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var2 + ';');
    ALTER TABLE [WorkflowTemplates] ALTER COLUMN [Status] tinyint NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    DECLARE @var3 nvarchar(max);
    SELECT @var3 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[WorkflowTemplates]') AND [c].[name] = N'CreatedAtUtc');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [WorkflowTemplates] DROP CONSTRAINT ' + @var3 + ';');
    ALTER TABLE [WorkflowTemplates] ALTER COLUMN [CreatedAtUtc] datetime2 NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [RequestTypes] DROP CONSTRAINT [FK_RequestTypes_WorkflowTemplates_WorkflowTemplateId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [Tickets] ADD [WorkflowTemplateId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    EXEC(N'
        UPDATE tk
        SET tk.[WorkflowTemplateId] = rt.[WorkflowTemplateId]
        FROM [Tickets] tk
        INNER JOIN [RequestTypes] rt
            ON rt.[RequestTypeId] = tk.[RequestTypeId]
        WHERE tk.[RequestTypeId] IS NOT NULL
          AND tk.[WorkflowTemplateId] IS NULL;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    EXEC sp_rename N'[RequestTypes].[WorkflowTemplateId]', N'WorkflowId', 'COLUMN';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    EXEC sp_rename N'[RequestTypes].[IX_RequestTypes_WorkflowTemplateId]', N'IX_RequestTypes_WorkflowId', 'INDEX';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    UPDATE rt
    SET rt.[WorkflowId] = t.[WorkflowId]
    FROM [RequestTypes] rt
    INNER JOIN [WorkflowTemplates] t ON t.[WorkflowTemplateId] = rt.[WorkflowId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    DROP INDEX [IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence] ON [WorkflowTemplateSteps];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplateSteps] ADD [ApprovalType] tinyint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE TABLE [WorkflowStepTransitions] (
        [WorkflowStepTransitionId] int NOT NULL IDENTITY,
        [WorkflowTemplateStepId] int NOT NULL,
        [Outcome] tinyint NOT NULL,
        [TargetWorkflowTemplateStepId] int NOT NULL,
        CONSTRAINT [PK_WorkflowStepTransitions] PRIMARY KEY ([WorkflowStepTransitionId]),
        CONSTRAINT [FK_WorkflowStepTransitions_WorkflowTemplateSteps_TargetWorkflowTemplateStepId] FOREIGN KEY ([TargetWorkflowTemplateStepId]) REFERENCES [WorkflowTemplateSteps] ([WorkflowTemplateStepId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_WorkflowStepTransitions_WorkflowTemplateSteps_WorkflowTemplateStepId] FOREIGN KEY ([WorkflowTemplateStepId]) REFERENCES [WorkflowTemplateSteps] ([WorkflowTemplateStepId]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE INDEX [IX_WorkflowTemplateSteps_WorkflowTemplateId_Sequence] ON [WorkflowTemplateSteps] ([WorkflowTemplateId], [Sequence]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE INDEX [IX_WorkflowTemplates_CreatedByEmployeeId] ON [WorkflowTemplates] ([CreatedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE INDEX [IX_WorkflowTemplates_PublishedByEmployeeId] ON [WorkflowTemplates] ([PublishedByEmployeeId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkflowTemplates_WorkflowId_VersionNumber] ON [WorkflowTemplates] ([WorkflowId], [VersionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_WorkflowTemplates_OneDraftPerWorkflow] ON [WorkflowTemplates] ([WorkflowId]) WHERE [Status] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_WorkflowTemplates_OnePublishedPerWorkflow] ON [WorkflowTemplates] ([WorkflowId]) WHERE [Status] = 2');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE INDEX [IX_Tickets_WorkflowTemplateId] ON [Tickets] ([WorkflowTemplateId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE INDEX [IX_WorkflowStepTransitions_TargetWorkflowTemplateStepId] ON [WorkflowStepTransitions] ([TargetWorkflowTemplateStepId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkflowStepTransitions_WorkflowTemplateStepId_Outcome] ON [WorkflowStepTransitions] ([WorkflowTemplateStepId], [Outcome]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [RequestTypes] ADD CONSTRAINT [FK_RequestTypes_Workflows_WorkflowId] FOREIGN KEY ([WorkflowId]) REFERENCES [Workflows] ([WorkflowId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [Tickets] ADD CONSTRAINT [FK_Tickets_WorkflowTemplates_WorkflowTemplateId] FOREIGN KEY ([WorkflowTemplateId]) REFERENCES [WorkflowTemplates] ([WorkflowTemplateId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD CONSTRAINT [FK_WorkflowTemplates_Employees_CreatedByEmployeeId] FOREIGN KEY ([CreatedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD CONSTRAINT [FK_WorkflowTemplates_Employees_PublishedByEmployeeId] FOREIGN KEY ([PublishedByEmployeeId]) REFERENCES [Employees] ([EmployeeId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    ALTER TABLE [WorkflowTemplates] ADD CONSTRAINT [FK_WorkflowTemplates_Workflows_WorkflowId] FOREIGN KEY ([WorkflowId]) REFERENCES [Workflows] ([WorkflowId]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260908054944_AddWorkflowVersioning'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260908054944_AddWorkflowVersioning', N'10.0.11');
END;

COMMIT;
GO

