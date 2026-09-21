SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[compliance].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'compliance') IS NULL EXEC(N'CREATE SCHEMA [compliance];');
    CREATE TABLE [compliance].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    IF SCHEMA_ID(N'compliance') IS NULL EXEC(N'CREATE SCHEMA [compliance];');
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    CREATE TABLE [compliance].[AddressScreening] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Address] varchar(128) NOT NULL,
        [Purpose] nvarchar(32) NOT NULL,
        [Provider] nvarchar(32) NOT NULL,
        [Decision] nvarchar(16) NOT NULL,
        [Score] int NULL,
        [RiskLevel] nvarchar(16) NULL,
        [ReasonsCsv] nvarchar(1024) NOT NULL,
        [AddressLabel] nvarchar(256) NULL,
        [ReportUrl] varchar(512) NULL,
        [RawResponse] nvarchar(max) NULL,
        [FailureReason] nvarchar(512) NULL,
        [PolicyDescription] nvarchar(256) NOT NULL,
        [ScreenedAt] datetimeoffset NOT NULL,
        [FreshUntil] datetimeoffset NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_AddressScreening] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    CREATE TABLE [compliance].[OutboxMessage] (
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
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    CREATE INDEX [IX_AddressScreening_Chain_Address_ScreenedAt] ON [compliance].[AddressScreening] ([Chain], [Address], [ScreenedAt] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    CREATE INDEX [IX_AddressScreening_Decision_ScreenedAt] ON [compliance].[AddressScreening] ([Decision], [ScreenedAt] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_AddressScreening_Seq] ON [compliance].[AddressScreening] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [compliance].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [compliance].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260910110006_InitialCompliance'
)
BEGIN
    INSERT INTO [compliance].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260910110006_InitialCompliance', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911094132_AddScreeningPolicy'
)
BEGIN
    CREATE TABLE [compliance].[ScreeningPolicyVersion] (
        [Id] uniqueidentifier NOT NULL,
        [BlockScore] int NOT NULL,
        [ReviewScore] int NOT NULL,
        [CacheDays] int NOT NULL,
        [IndirectReviewMaxHops] int NOT NULL,
        [IndirectReviewMinPercent] decimal(5,2) NOT NULL,
        [ExtraAlwaysBlockIndicatorsCsv] nvarchar(1024) NOT NULL,
        [Note] nvarchar(512) NULL,
        [UpdatedBy] nvarchar(128) NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_ScreeningPolicyVersion] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911094132_AddScreeningPolicy'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_ScreeningPolicyVersion_Seq] ON [compliance].[ScreeningPolicyVersion] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911094132_AddScreeningPolicy'
)
BEGIN
    CREATE INDEX [IX_ScreeningPolicyVersion_UpdatedAt] ON [compliance].[ScreeningPolicyVersion] ([UpdatedAt] DESC);
END;

IF NOT EXISTS (
    SELECT * FROM [compliance].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260911094132_AddScreeningPolicy'
)
BEGIN
    INSERT INTO [compliance].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260911094132_AddScreeningPolicy', N'10.0.9');
END;

COMMIT;
GO

