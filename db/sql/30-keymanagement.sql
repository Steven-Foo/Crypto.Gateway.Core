IF OBJECT_ID(N'[keymgmt].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'keymgmt') IS NULL EXEC(N'CREATE SCHEMA [keymgmt];');
    CREATE TABLE [keymgmt].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    IF SCHEMA_ID(N'keymgmt') IS NULL EXEC(N'CREATE SCHEMA [keymgmt];');
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE TABLE [keymgmt].[HdWallet] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Purpose] nvarchar(16) NOT NULL,
        [Scheme] nvarchar(24) NOT NULL,
        [SecretProvider] nvarchar(32) NOT NULL,
        [SecretReference] varchar(512) NOT NULL,
        [PublicKeyReference] varchar(512) NULL,
        [DerivationPath] varchar(64) NOT NULL,
        [NextDerivationIndex] bigint NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [Description] nvarchar(256) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_HdWallet] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_HdWallet_DerivationIndex_Range] CHECK ([NextDerivationIndex] >= 0 AND [NextDerivationIndex] <= 2147483648),
        CONSTRAINT [CK_HdWallet_PublicKeyReference_MatchesScheme] CHECK (([Scheme] = 'Bip32Secp256k1' AND [PublicKeyReference] IS NOT NULL) OR ([Scheme] = 'Slip10Ed25519' AND [PublicKeyReference] IS NULL))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE TABLE [keymgmt].[OutboxMessage] (
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
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE TABLE [keymgmt].[DerivedKey] (
        [Id] uniqueidentifier NOT NULL,
        [HdWalletId] uniqueidentifier NOT NULL,
        [DerivationIndex] bigint NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Address] varchar(128) NOT NULL,
        [DerivationPath] varchar(80) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_DerivedKey] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [CK_DerivedKey_DerivationIndex_Range] CHECK ([DerivationIndex] >= 0 AND [DerivationIndex] <= 2147483647),
        CONSTRAINT [FK_DerivedKey_HdWallet_HdWalletId] FOREIGN KEY ([HdWalletId]) REFERENCES [keymgmt].[HdWallet] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DerivedKey_Chain_Address] ON [keymgmt].[DerivedKey] ([Chain], [Address]);
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DerivedKey_HdWalletId_DerivationIndex] ON [keymgmt].[DerivedKey] ([HdWalletId], [DerivationIndex]);
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_DerivedKey_Seq] ON [keymgmt].[DerivedKey] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_HdWallet_Chain_Purpose] ON [keymgmt].[HdWallet] ([Chain], [Purpose]) WHERE [Status] = ''Active''');
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [keymgmt].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [keymgmt].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [keymgmt].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260710112633_InitialKeyManagement'
)
BEGIN
    INSERT INTO [keymgmt].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260710112633_InitialKeyManagement', N'10.0.9');
END;

COMMIT;
GO

