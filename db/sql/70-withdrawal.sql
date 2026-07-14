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

