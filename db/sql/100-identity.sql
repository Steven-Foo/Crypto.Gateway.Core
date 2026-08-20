SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[identity].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'identity') IS NULL EXEC(N'CREATE SCHEMA [identity];');
    CREATE TABLE [identity].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    IF SCHEMA_ID(N'identity') IS NULL EXEC(N'CREATE SCHEMA [identity];');
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE TABLE [identity].[OutboxMessage] (
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
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE TABLE [identity].[StaffSession] (
        [Id] uniqueidentifier NOT NULL,
        [StaffUserId] uniqueidentifier NOT NULL,
        [TokenHash] varchar(128) NOT NULL,
        [Role] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_StaffSession] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE TABLE [identity].[StaffUser] (
        [Id] uniqueidentifier NOT NULL,
        [Username] nvarchar(64) NOT NULL,
        [PasswordHash] nvarchar(512) NOT NULL,
        [Role] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_StaffUser] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [identity].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [identity].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_StaffSession_Seq] ON [identity].[StaffSession] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE INDEX [IX_StaffSession_StaffUserId] ON [identity].[StaffSession] ([StaffUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE UNIQUE INDEX [IX_StaffSession_TokenHash] ON [identity].[StaffSession] ([TokenHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    CREATE UNIQUE INDEX [IX_StaffUser_Username] ON [identity].[StaffUser] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260721072024_InitialIdentity'
)
BEGIN
    INSERT INTO [identity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260721072024_InitialIdentity', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[identity].[StaffSession]') AND [c].[name] = N'Role');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [identity].[StaffSession] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [identity].[StaffSession] DROP COLUMN [Role];
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[identity].[StaffUser]') AND [c].[name] = N'Role');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [identity].[StaffUser] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [identity].[StaffUser] DROP COLUMN [Role];
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    ALTER TABLE [identity].[StaffUser] ADD [Status] nvarchar(16) NOT NULL DEFAULT N'Active';
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    ALTER TABLE [identity].[StaffUser] ADD [RoleId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    ALTER TABLE [identity].[StaffSession] ADD [PermissionCodesCsv] varchar(2048) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    ALTER TABLE [identity].[StaffSession] ADD [RoleId] uniqueidentifier NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    ALTER TABLE [identity].[StaffSession] ADD [RoleName] nvarchar(64) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    CREATE TABLE [identity].[Role] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(64) NOT NULL,
        [Description] nvarchar(256) NULL,
        [PermissionCodesCsv] varchar(2048) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Role] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    CREATE INDEX [IX_StaffUser_RoleId] ON [identity].[StaffUser] ([RoleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Role_Name] ON [identity].[Role] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    ALTER TABLE [identity].[StaffUser] ADD CONSTRAINT [FK_StaffUser_Role_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [identity].[Role] ([Id]) ON DELETE NO ACTION;
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818035956_AddRolesAndPermissions'
)
BEGIN
    INSERT INTO [identity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260818035956_AddRolesAndPermissions', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818081353_AddStaffSessionUsername'
)
BEGIN
    ALTER TABLE [identity].[StaffSession] ADD [Username] nvarchar(64) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818081353_AddStaffSessionUsername'
)
BEGIN
    INSERT INTO [identity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260818081353_AddStaffSessionUsername', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820084841_AddStaffSessionCsrfToken'
)
BEGIN
    ALTER TABLE [identity].[StaffSession] ADD [CsrfToken] varchar(128) NOT NULL DEFAULT '';
END;

IF NOT EXISTS (
    SELECT * FROM [identity].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260820084841_AddStaffSessionCsrfToken'
)
BEGIN
    INSERT INTO [identity].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260820084841_AddStaffSessionCsrfToken', N'10.0.9');
END;

COMMIT;
GO

