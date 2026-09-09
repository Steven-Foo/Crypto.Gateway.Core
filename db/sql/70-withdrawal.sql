SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[withdrawal].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'withdrawal') IS NULL EXEC(N'CREATE SCHEMA [withdrawal];');
    CREATE TABLE [withdrawal].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    IF SCHEMA_ID(N'withdrawal') IS NULL EXEC(N'CREATE SCHEMA [withdrawal];');
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE TABLE [withdrawal].[OutboxMessage] (
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
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE TABLE [withdrawal].[Withdrawal] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [DestinationAddress] varchar(128) NOT NULL,
        [Amount] decimal(38,0) NOT NULL,
        [Fee] decimal(38,0) NOT NULL,
        [IdempotencyKey] varchar(128) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [ApprovedBy] nvarchar(128) NULL,
        [SigningRequestId] uniqueidentifier NULL,
        [TransactionHash] varchar(128) NULL,
        [FailureReason] nvarchar(512) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_Withdrawal] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [withdrawal].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [withdrawal].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE INDEX [IX_Withdrawal_Merchant] ON [withdrawal].[Withdrawal] ([MerchantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_Withdrawal_Seq] ON [withdrawal].[Withdrawal] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE INDEX [IX_Withdrawal_Status] ON [withdrawal].[Withdrawal] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Withdrawal_Idempotency] ON [withdrawal].[Withdrawal] ([MerchantId], [IdempotencyKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714030859_InitialWithdrawal'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260714030859_InitialWithdrawal', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260723103610_AddWithdrawalSignedTransaction'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [SignedTransaction] varbinary(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260723103610_AddWithdrawalSignedTransaction'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260723103610_AddWithdrawalSignedTransaction', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729091105_AddWithdrawalConfirmations'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [Confirmations] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729091105_AddWithdrawalConfirmations'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260729091105_AddWithdrawalConfirmations', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729105913_AddWithdrawalCallbackUrl'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [CallbackUrl] nvarchar(512) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729105913_AddWithdrawalCallbackUrl'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260729105913_AddWithdrawalCallbackUrl', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811060644_AddWithdrawalFundingHold'
)
BEGIN
    DROP INDEX [IX_Withdrawal_Status] ON [withdrawal].[Withdrawal];
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[withdrawal].[Withdrawal]') AND [c].[name] = N'Status');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [withdrawal].[Withdrawal] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [withdrawal].[Withdrawal] ALTER COLUMN [Status] nvarchar(24) NOT NULL;
    CREATE INDEX [IX_Withdrawal_Status] ON [withdrawal].[Withdrawal] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811060644_AddWithdrawalFundingHold'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [ReleasedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811060644_AddWithdrawalFundingHold'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [ReleasedBy] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811060644_AddWithdrawalFundingHold'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [StatusReason] nvarchar(512) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811060644_AddWithdrawalFundingHold'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260811060644_AddWithdrawalFundingHold', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811094910_AddWithdrawalSourceWallet'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [SourceWalletId] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811094910_AddWithdrawalSourceWallet'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Withdrawal_InFlight_SourceWallet] ON [withdrawal].[Withdrawal] ([SourceWalletId]) WHERE [Status] IN (''Signing'', ''Broadcast'')');
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811094910_AddWithdrawalSourceWallet'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260811094910_AddWithdrawalSourceWallet', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260814080834_RenameWithdrawalIdempotencyKeyToMerchantTransactionId'
)
BEGIN
    EXEC sp_rename N'[withdrawal].[Withdrawal].[IdempotencyKey]', N'MerchantTransactionId', 'COLUMN';
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260814080834_RenameWithdrawalIdempotencyKeyToMerchantTransactionId'
)
BEGIN
    EXEC sp_rename N'[withdrawal].[Withdrawal].[UX_Withdrawal_Idempotency]', N'UX_Withdrawal_MerchantTxn', 'INDEX';
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260814080834_RenameWithdrawalIdempotencyKeyToMerchantTransactionId'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260814080834_RenameWithdrawalIdempotencyKeyToMerchantTransactionId', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818061933_AddWithdrawalKind'
)
BEGIN
    DROP INDEX [UX_Withdrawal_MerchantTxn] ON [withdrawal].[Withdrawal];
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818061933_AddWithdrawalKind'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [Kind] nvarchar(16) NOT NULL DEFAULT ('User');
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818061933_AddWithdrawalKind'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Withdrawal_MerchantTxn] ON [withdrawal].[Withdrawal] ([MerchantId], [Kind], [MerchantTransactionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260818061933_AddWithdrawalKind'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260818061933_AddWithdrawalKind', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826032039_AddMerchantPayoutApproval'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [MerchantApprovedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826032039_AddMerchantPayoutApproval'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [MerchantApprovedBy] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260826032039_AddMerchantPayoutApproval'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260826032039_AddMerchantPayoutApproval', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828033940_AddWithdrawalEnergyUsed'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [EnergyUsed] decimal(38,0) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828033940_AddWithdrawalEnergyUsed'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260828033940_AddWithdrawalEnergyUsed', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [AuditedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [AuditedBy] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [CompletedAt] datetimeoffset NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [CompletedBy] nvarchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [SettlementSourceAddress] varchar(128) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    CREATE TABLE [withdrawal].[HotWalletTopUp] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [TargetWalletId] uniqueidentifier NOT NULL,
        [TargetAddress] varchar(128) NOT NULL,
        [Amount] decimal(38,0) NOT NULL,
        [TransactionHash] varchar(128) NOT NULL,
        [SourceAddress] varchar(128) NULL,
        [RecordedBy] nvarchar(128) NOT NULL,
        [RecordedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_HotWalletTopUp] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Withdrawal_SettlementTxHash] ON [withdrawal].[Withdrawal] ([TransactionHash]) WHERE [SettlementSourceAddress] IS NOT NULL AND [TransactionHash] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    CREATE INDEX [IX_HotWalletTopUp_Chain_RecordedAt] ON [withdrawal].[HotWalletTopUp] ([Chain], [RecordedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_HotWalletTopUp_Seq] ON [withdrawal].[HotWalletTopUp] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    CREATE UNIQUE INDEX [UX_HotWalletTopUp_TxHash] ON [withdrawal].[HotWalletTopUp] ([TransactionHash]);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260828104824_AddManualSettlementAndTopUp'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260828104824_AddManualSettlementAndTopUp', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909073418_AddWithdrawalFeeMinimumSnapshot'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [FeeBps] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909073418_AddWithdrawalFeeMinimumSnapshot'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [FeeFixed] decimal(38,0) NOT NULL DEFAULT (0);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909073418_AddWithdrawalFeeMinimumSnapshot'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [FeeMinimum] decimal(38,0) NOT NULL DEFAULT (0);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909073418_AddWithdrawalFeeMinimumSnapshot'
)
BEGIN
    ALTER TABLE [withdrawal].[Withdrawal] ADD [MinimumFeeApplied] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [withdrawal].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260909073418_AddWithdrawalFeeMinimumSnapshot'
)
BEGIN
    INSERT INTO [withdrawal].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260909073418_AddWithdrawalFeeMinimumSnapshot', N'10.0.9');
END;

COMMIT;
GO

