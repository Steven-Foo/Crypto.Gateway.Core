SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[wallet].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'wallet') IS NULL EXEC(N'CREATE SCHEMA [wallet];');
    CREATE TABLE [wallet].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    IF SCHEMA_ID(N'wallet') IS NULL EXEC(N'CREATE SCHEMA [wallet];');
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE TABLE [wallet].[OutboxMessage] (
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
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE TABLE [wallet].[Wallet] (
        [Id] uniqueidentifier NOT NULL,
        [DerivedKeyId] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Address] varchar(128) NOT NULL,
        [WalletType] nvarchar(24) NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Status] nvarchar(16) NOT NULL,
        [Description] nvarchar(256) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Wallet] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE TABLE [wallet].[WalletAssignment] (
        [Id] uniqueidentifier NOT NULL,
        [WalletId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [AssignedAt] datetimeoffset NOT NULL,
        [ReleasedAt] datetimeoffset NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_WalletAssignment] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [FK_WalletAssignment_Wallet_WalletId] FOREIGN KEY ([WalletId]) REFERENCES [wallet].[Wallet] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [wallet].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [wallet].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Wallet_Chain_Address] ON [wallet].[Wallet] ([Chain], [Address]);
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Wallet_DerivedKeyId] ON [wallet].[Wallet] ([DerivedKeyId]);
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Wallet_MerchantId] ON [wallet].[Wallet] ([MerchantId]) WHERE [MerchantId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE INDEX [IX_WalletAssignment_MerchantId] ON [wallet].[WalletAssignment] ([MerchantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_WalletAssignment_Seq] ON [wallet].[WalletAssignment] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_WalletAssignment_WalletId] ON [wallet].[WalletAssignment] ([WalletId]) WHERE [Status] = ''Active''');
END;

IF NOT EXISTS (
    SELECT * FROM [wallet].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713031326_InitialWallet'
)
BEGIN
    INSERT INTO [wallet].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260713031326_InitialWallet', N'10.0.9');
END;

COMMIT;
GO

