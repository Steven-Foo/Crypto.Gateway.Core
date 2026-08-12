SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[sweep].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'sweep') IS NULL EXEC(N'CREATE SCHEMA [sweep];');
    CREATE TABLE [sweep].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    IF SCHEMA_ID(N'sweep') IS NULL EXEC(N'CREATE SCHEMA [sweep];');
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    CREATE TABLE [sweep].[OutboxMessage] (
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
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    CREATE TABLE [sweep].[Sweep] (
        [Id] uniqueidentifier NOT NULL,
        [WalletId] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [FromAddress] varchar(128) NOT NULL,
        [ToAddress] varchar(128) NOT NULL,
        [Amount] decimal(38,0) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [SigningRequestId] uniqueidentifier NULL,
        [SignedTransaction] varbinary(max) NULL,
        [TransactionHash] varchar(128) NULL,
        [FailureReason] nvarchar(512) NULL,
        [Confirmations] int NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_Sweep] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [sweep].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [sweep].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_Sweep_Seq] ON [sweep].[Sweep] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    CREATE INDEX [IX_Sweep_Status] ON [sweep].[Sweep] ([Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_Sweep_InFlight_Wallet_Asset] ON [sweep].[Sweep] ([WalletId], [AssetId]) WHERE [Status] IN (''Pending'', ''Signing'', ''Broadcast'')');
END;

IF NOT EXISTS (
    SELECT * FROM [sweep].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260804101729_InitialSweep'
)
BEGIN
    INSERT INTO [sweep].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260804101729_InitialSweep', N'10.0.9');
END;

COMMIT;
GO

