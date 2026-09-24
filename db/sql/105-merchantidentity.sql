SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[merchantidentity].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'merchantidentity') IS NULL EXEC(N'CREATE SCHEMA [merchantidentity];');
    CREATE TABLE [merchantidentity].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    IF SCHEMA_ID(N'merchantidentity') IS NULL EXEC(N'CREATE SCHEMA [merchantidentity];');
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE TABLE [merchantidentity].[MerchantUser] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Username] nvarchar(64) NOT NULL,
        [DisplayName] nvarchar(128) NOT NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_MerchantUser] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE TABLE [merchantidentity].[MerchantUserSession] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantUserId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Username] nvarchar(64) NOT NULL,
        [DisplayName] nvarchar(128) NOT NULL,
        [TokenHash] varchar(128) NOT NULL,
        [CsrfToken] varchar(128) NOT NULL,
        [PermissionCodesCsv] varchar(2048) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_MerchantUserSession] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE TABLE [merchantidentity].[OutboxMessage] (
        [Id] uniqueidentifier NOT NULL,
        [Type] nvarchar(512) NOT NULL,
        [Content] nvarchar(max) NOT NULL,
        [OccurredOnUtc] datetimeoffset NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ProcessedOnUtc] datetimeoffset NULL,
        [RetryCount] int NOT NULL,
        [Error] nvarchar(2048) NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_OutboxMessage] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE INDEX [IX_MerchantUser_MerchantId] ON [merchantidentity].[MerchantUser] ([MerchantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantUser_Username] ON [merchantidentity].[MerchantUser] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE INDEX [IX_MerchantUserSession_MerchantUserId] ON [merchantidentity].[MerchantUserSession] ([MerchantUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_MerchantUserSession_Seq] ON [merchantidentity].[MerchantUserSession] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantUserSession_TokenHash] ON [merchantidentity].[MerchantUserSession] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [merchantidentity].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [merchantidentity].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260821074812_InitialMerchantIdentity'
)
BEGIN
    INSERT INTO [merchantidentity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260821074812_InitialMerchantIdentity', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    ALTER TABLE [merchantidentity].[MerchantUser] ADD [MustChangePassword] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    ALTER TABLE [merchantidentity].[MerchantUser] ADD [RoleId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    CREATE TABLE [merchantidentity].[MerchantRole] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(64) NOT NULL,
        [Description] nvarchar(256) NULL,
        [PermissionCodesCsv] varchar(2048) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_MerchantRole] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    CREATE INDEX [IX_MerchantUser_RoleId] ON [merchantidentity].[MerchantUser] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [UX_MerchantRole_Merchant_Name] ON [merchantidentity].[MerchantRole] ([MerchantId], [Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    ALTER TABLE [merchantidentity].[MerchantUser] ADD CONSTRAINT [FK_MerchantUser_MerchantRole_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [merchantidentity].[MerchantRole] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826024428_AddMerchantRolesAndAccountLifecycle'
)
BEGIN
    INSERT INTO [merchantidentity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260826024428_AddMerchantRolesAndAccountLifecycle', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923103509_AddMerchantUserPrimaryFlag'
)
BEGIN
    ALTER TABLE [merchantidentity].[MerchantUser] ADD [IsPrimary] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923103509_AddMerchantUserPrimaryFlag'
)
BEGIN
    EXEC(N'
    ;WITH Ranked AS (
        SELECT [Id],
               ROW_NUMBER() OVER (PARTITION BY [MerchantId] ORDER BY [CreatedAt] ASC) AS [Rn]
        FROM [merchantidentity].[MerchantUser]
    )
    UPDATE [u]
    SET [u].[IsPrimary] = 1
    FROM [merchantidentity].[MerchantUser] AS [u]
    INNER JOIN [Ranked] ON [Ranked].[Id] = [u].[Id]
    WHERE [Ranked].[Rn] = 1;
    ');
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923103509_AddMerchantUserPrimaryFlag'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_MerchantUser_MerchantId_Primary] ON [merchantidentity].[MerchantUser] ([MerchantId]) WHERE [IsPrimary] = 1');
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260923103509_AddMerchantUserPrimaryFlag'
)
BEGIN
    INSERT INTO [merchantidentity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260923103509_AddMerchantUserPrimaryFlag', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    ALTER TABLE [merchantidentity].[MerchantUserSession] ADD [TwoFactorMethod] varchar(16) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    CREATE TABLE [merchantidentity].[MerchantUserRecoveryCode] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantUserId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [CodeHash] varchar(256) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UsedAt] datetimeoffset NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_MerchantUserRecoveryCode] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    CREATE TABLE [merchantidentity].[MerchantUserTwoFactor] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantUserId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [SecretCiphertext] varchar(512) NOT NULL,
        [Status] varchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [EnrolledAt] datetimeoffset NULL,
        [FailedAttempts] int NOT NULL,
        [LockedUntil] datetimeoffset NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_MerchantUserTwoFactor] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    CREATE INDEX [IX_MerchantUserRecoveryCode_MerchantUserId_UsedAt] ON [merchantidentity].[MerchantUserRecoveryCode] ([MerchantUserId], [UsedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_MerchantUserRecoveryCode_Seq] ON [merchantidentity].[MerchantUserRecoveryCode] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    CREATE INDEX [IX_MerchantUserTwoFactor_MerchantId_MerchantUserId] ON [merchantidentity].[MerchantUserTwoFactor] ([MerchantId], [MerchantUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantUserTwoFactor_MerchantUserId] ON [merchantidentity].[MerchantUserTwoFactor] ([MerchantUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchantidentity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260924090557_AddMerchantUserTwoFactor'
)
BEGIN
    INSERT INTO [merchantidentity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260924090557_AddMerchantUserTwoFactor', N'10.0.9');
END;

COMMIT;
GO

