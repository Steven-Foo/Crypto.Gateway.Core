IF OBJECT_ID(N'[merchant].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'merchant') IS NULL EXEC(N'CREATE SCHEMA [merchant];');
    CREATE TABLE [merchant].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    IF SCHEMA_ID(N'merchant') IS NULL EXEC(N'CREATE SCHEMA [merchant];');
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE TABLE [merchant].[Merchant] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantCode] varchar(64) NOT NULL,
        [Name] nvarchar(256) NOT NULL,
        [CallbackUrl] nvarchar(512) NULL,
        [Status] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Merchant] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE TABLE [merchant].[MerchantWebhook] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [EventType] nvarchar(64) NOT NULL,
        [Payload] nvarchar(max) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [RetryCount] int NOT NULL,
        [NextRetryAt] datetimeoffset NULL,
        [LastResponse] nvarchar(1024) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_MerchantWebhook] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE TABLE [merchant].[OutboxMessage] (
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
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE TABLE [merchant].[MerchantApiCredential] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [ApiKey] varchar(64) NOT NULL,
        [SecretHash] varchar(256) NOT NULL,
        [HashVersion] int NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [RevokedAt] datetimeoffset NULL,
        CONSTRAINT [PK_MerchantApiCredential] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MerchantApiCredential_Merchant_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merchant].[Merchant] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE TABLE [merchant].[MerchantAssetPolicy] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [SweepThreshold] decimal(38,0) NOT NULL,
        [MinimumWithdrawal] decimal(38,0) NOT NULL,
        [MaximumWithdrawal] decimal(38,0) NULL,
        [WithdrawalFee] decimal(38,0) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_MerchantAssetPolicy] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_MerchantAssetPolicy_NonNegative] CHECK ([SweepThreshold] >= 0 AND [MinimumWithdrawal] >= 0 AND [WithdrawalFee] >= 0 AND ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= 0)),
        CONSTRAINT [CK_MerchantAssetPolicy_WithdrawalRange] CHECK ([MaximumWithdrawal] IS NULL OR [MaximumWithdrawal] >= [MinimumWithdrawal]),
        CONSTRAINT [FK_MerchantAssetPolicy_Merchant_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merchant].[Merchant] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE TABLE [merchant].[MerchantConfiguration] (
        [Id] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NOT NULL,
        [AutoSweepEnabled] bit NOT NULL,
        [WebhookRetryCount] int NOT NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_MerchantConfiguration] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_MerchantConfiguration_WebhookRetryCount] CHECK ([WebhookRetryCount] >= 0 AND [WebhookRetryCount] <= 20),
        CONSTRAINT [FK_MerchantConfiguration_Merchant_MerchantId] FOREIGN KEY ([MerchantId]) REFERENCES [merchant].[Merchant] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Merchant_MerchantCode] ON [merchant].[Merchant] ([MerchantCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantApiCredential_ApiKey] ON [merchant].[MerchantApiCredential] ([ApiKey]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE INDEX [IX_MerchantApiCredential_MerchantId_Status] ON [merchant].[MerchantApiCredential] ([MerchantId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantAssetPolicy_MerchantId_AssetId] ON [merchant].[MerchantAssetPolicy] ([MerchantId], [AssetId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE UNIQUE INDEX [IX_MerchantConfiguration_MerchantId] ON [merchant].[MerchantConfiguration] ([MerchantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE INDEX [IX_MerchantWebhook_MerchantId] ON [merchant].[MerchantWebhook] ([MerchantId]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_MerchantWebhook_Seq] ON [merchant].[MerchantWebhook] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_MerchantWebhook_Status_NextRetryAt] ON [merchant].[MerchantWebhook] ([Status], [NextRetryAt]) WHERE [NextRetryAt] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [merchant].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [merchant].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [merchant].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710100512_InitialMerchant'
)
BEGIN
    INSERT INTO [merchant].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260710100512_InitialMerchant', N'10.0.9');
END;

COMMIT;
GO

