SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[paymentintent].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'paymentintent') IS NULL EXEC(N'CREATE SCHEMA [paymentintent];');
    CREATE TABLE [paymentintent].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    IF SCHEMA_ID(N'paymentintent') IS NULL EXEC(N'CREATE SCHEMA [paymentintent];');
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE TABLE [paymentintent].[OutboxMessage] (
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
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE TABLE [paymentintent].[PaymentIntent] (
        [Id] uniqueidentifier NOT NULL,
        [PublicReference] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [MerchantTransactionId] varchar(128) NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [WalletId] uniqueidentifier NOT NULL,
        [Address] varchar(128) NOT NULL,
        [ExpectedAmount] decimal(38,0) NOT NULL,
        [CallbackUrl] nvarchar(512) NULL,
        [Status] nvarchar(16) NOT NULL,
        [MatchedDepositId] uniqueidentifier NULL,
        [AmountMatched] bit NULL,
        [ExpiresAt] datetimeoffset NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_PaymentIntent] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [paymentintent].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [paymentintent].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE INDEX [IX_PaymentIntent_MatchedDeposit] ON [paymentintent].[PaymentIntent] ([MatchedDepositId]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_PaymentIntent_Seq] ON [paymentintent].[PaymentIntent] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE INDEX [IX_PaymentIntent_Status_Expiry] ON [paymentintent].[PaymentIntent] ([Status], [ExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE UNIQUE INDEX [UX_PaymentIntent_Idempotency] ON [paymentintent].[PaymentIntent] ([MerchantId], [MerchantTransactionId]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UX_PaymentIntent_LiveWallet] ON [paymentintent].[PaymentIntent] ([WalletId]) WHERE [Status] = ''Waiting''');
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    CREATE UNIQUE INDEX [UX_PaymentIntent_PublicRef] ON [paymentintent].[PaymentIntent] ([PublicReference]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260714082137_InitialPaymentIntent'
)
BEGIN
    INSERT INTO [paymentintent].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260714082137_InitialPaymentIntent', N'10.0.9');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260716060225_AddPaymentIntentGracePeriod'
)
BEGIN
    DROP INDEX [IX_PaymentIntent_Status_Expiry] ON [paymentintent].[PaymentIntent];
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260716060225_AddPaymentIntentGracePeriod'
)
BEGIN
    ALTER TABLE [paymentintent].[PaymentIntent] ADD [GraceExpiresAt] datetimeoffset NOT NULL DEFAULT '0001-01-01T00:00:00.0000000+00:00';
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260716060225_AddPaymentIntentGracePeriod'
)
BEGIN
    CREATE INDEX [IX_PaymentIntent_Status_GraceExpiry] ON [paymentintent].[PaymentIntent] ([Status], [GraceExpiresAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [paymentintent].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260716060225_AddPaymentIntentGracePeriod'
)
BEGIN
    INSERT INTO [paymentintent].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260716060225_AddPaymentIntentGracePeriod', N'10.0.9');
END;

COMMIT;
GO

