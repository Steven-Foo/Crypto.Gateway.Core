SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[treasury].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'treasury') IS NULL EXEC(N'CREATE SCHEMA [treasury];');
    CREATE TABLE [treasury].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    IF SCHEMA_ID(N'treasury') IS NULL EXEC(N'CREATE SCHEMA [treasury];');
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE TABLE [treasury].[OutboxMessage] (
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
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE TABLE [treasury].[TreasuryColdWallet] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Address] varchar(128) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_TreasuryColdWallet] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE TABLE [treasury].[TreasuryReload] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [SourceAddress] varchar(128) NOT NULL,
        [TargetWalletId] uniqueidentifier NOT NULL,
        [TargetAddress] varchar(128) NOT NULL,
        [Amount] decimal(38,0) NOT NULL,
        [Status] nvarchar(24) NOT NULL,
        [UnsignedPayload] varbinary(max) NOT NULL,
        [SignedTransaction] varbinary(max) NULL,
        [TransactionHash] varchar(128) NULL,
        [Confirmations] int NULL,
        [StatusReason] nvarchar(512) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_TreasuryReload] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [treasury].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [treasury].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TreasuryColdWallet_Chain] ON [treasury].[TreasuryColdWallet] ([Chain]);
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_TreasuryReload_Seq] ON [treasury].[TreasuryReload] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    CREATE INDEX [IX_TreasuryReload_Status] ON [treasury].[TreasuryReload] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260812035026_InitialTreasury'
)
BEGIN
    INSERT INTO [treasury].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260812035026_InitialTreasury', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260917035503_RemoveTreasuryReload'
)
BEGIN
    DROP TABLE [treasury].[TreasuryReload];
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260917035503_RemoveTreasuryReload'
)
BEGIN
    INSERT INTO [treasury].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260917035503_RemoveTreasuryReload', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    DROP INDEX [UX_TreasuryColdWallet_Chain] ON [treasury].[TreasuryColdWallet];
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [Kind] nvarchar(16) NOT NULL DEFAULT N'Safe';
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [Label] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [ScreenedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [ScreeningDecision] varchar(16) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [ScreeningId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [ScreeningScore] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    ALTER TABLE [treasury].[TreasuryColdWallet] ADD [Status] nvarchar(16) NOT NULL DEFAULT N'Active';
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    CREATE UNIQUE INDEX [UX_TreasuryColdWallet_Chain_Address] ON [treasury].[TreasuryColdWallet] ([Chain], [Address]);
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_TreasuryColdWallet_Chain_Kind_Active] ON [treasury].[TreasuryColdWallet] ([Chain], [Kind]) WHERE [Status] = ''Active''');
END;

IF NOT EXISTS (
    SELECT * FROM [treasury].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260918032022_ColdCollectionWallets'
)
BEGIN
    INSERT INTO [treasury].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260918032022_ColdCollectionWallets', N'10.0.9');
END;

COMMIT;
GO

