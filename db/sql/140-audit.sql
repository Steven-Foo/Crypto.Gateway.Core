SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[audit].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA [audit];');
    CREATE TABLE [audit].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    IF SCHEMA_ID(N'audit') IS NULL EXEC(N'CREATE SCHEMA [audit];');
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE TABLE [audit].[AuditEntry] (
        [Id] uniqueidentifier NOT NULL,
        [StaffUserId] uniqueidentifier NOT NULL,
        [StaffUsername] nvarchar(64) NOT NULL,
        [Action] nvarchar(128) NOT NULL,
        [EntityType] nvarchar(64) NOT NULL,
        [EntityId] nvarchar(128) NULL,
        [Reason] nvarchar(512) NULL,
        [IpAddress] varchar(64) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AuditEntry] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE TABLE [audit].[OutboxMessage] (
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
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE INDEX [IX_AuditEntry_Action] ON [audit].[AuditEntry] ([Action]);
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE INDEX [IX_AuditEntry_CreatedAt] ON [audit].[AuditEntry] ([CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE INDEX [IX_AuditEntry_EntityType_EntityId] ON [audit].[AuditEntry] ([EntityType], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_AuditEntry_Seq] ON [audit].[AuditEntry] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE INDEX [IX_AuditEntry_StaffUserId] ON [audit].[AuditEntry] ([StaffUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [audit].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [audit].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [audit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818083052_InitialAudit'
)
BEGIN
    INSERT INTO [audit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260818083052_InitialAudit', N'10.0.9');
END;

COMMIT;
GO

