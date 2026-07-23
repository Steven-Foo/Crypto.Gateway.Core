IF OBJECT_ID(N'[deposit].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'deposit') IS NULL EXEC(N'CREATE SCHEMA [deposit];');
    CREATE TABLE [deposit].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    IF SCHEMA_ID(N'deposit') IS NULL EXEC(N'CREATE SCHEMA [deposit];');
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE TABLE [deposit].[Deposit] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Address] varchar(128) NOT NULL,
        [WalletId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [Amount] decimal(38,0) NOT NULL,
        [TransactionHash] varchar(128) NOT NULL,
        [OutputIndex] int NOT NULL,
        [BlockNumber] bigint NOT NULL,
        [BlockHash] varchar(128) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [Confirmations] int NOT NULL,
        [DetectedAt] datetimeoffset NOT NULL,
        [ConfirmedAt] datetimeoffset NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_Deposit] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE TABLE [deposit].[OutboxMessage] (
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
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE TABLE [deposit].[ScanCursor] (
        [Chain] nvarchar(16) NOT NULL,
        [LastScannedBlock] bigint NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_ScanCursor] PRIMARY KEY ([Chain])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE INDEX [IX_Deposit_Chain_Status] ON [deposit].[Deposit] ([Chain], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE INDEX [IX_Deposit_Merchant] ON [deposit].[Deposit] ([MerchantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_Deposit_Seq] ON [deposit].[Deposit] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Deposit_Tx] ON [deposit].[Deposit] ([Chain], [TransactionHash], [OutputIndex]);
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [deposit].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [deposit].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713082649_InitialDeposit'
)
BEGIN
    INSERT INTO [deposit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260713082649_InitialDeposit', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260717072121_DepositFinalizedAt'
)
BEGIN
    DROP INDEX [IX_Deposit_Chain_Status] ON [deposit].[Deposit];
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260717072121_DepositFinalizedAt'
)
BEGIN
    ALTER TABLE [deposit].[Deposit] ADD [FinalizedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260717072121_DepositFinalizedAt'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Deposit_Chain_Status] ON [deposit].[Deposit] ([Chain], [Status]) WHERE [FinalizedAt] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260717072121_DepositFinalizedAt'
)
BEGIN
    INSERT INTO [deposit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260717072121_DepositFinalizedAt', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260723073311_AddDepositAddressStatusIndex'
)
BEGIN
    CREATE INDEX [IX_Deposit_Chain_Address_Status] ON [deposit].[Deposit] ([Chain], [Address], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [deposit].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260723073311_AddDepositAddressStatusIndex'
)
BEGIN
    INSERT INTO [deposit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260723073311_AddDepositAddressStatusIndex', N'10.0.9');
END;

COMMIT;
GO

