IF OBJECT_ID(N'[blockchain].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'blockchain') IS NULL EXEC(N'CREATE SCHEMA [blockchain];');
    CREATE TABLE [blockchain].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    IF SCHEMA_ID(N'blockchain') IS NULL EXEC(N'CREATE SCHEMA [blockchain];');
END;

IF NOT EXISTS (
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    CREATE TABLE [blockchain].[Asset] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [Symbol] nvarchar(16) NOT NULL,
        [ContractAddress] varchar(128) NULL,
        [Decimals] int NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Asset] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Asset_Decimals_Range] CHECK ([Decimals] >= 0 AND [Decimals] <= 38)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    CREATE TABLE [blockchain].[OutboxMessage] (
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
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Asset_Chain_Symbol_ContractAddress] ON [blockchain].[Asset] ([Chain], [Symbol], [ContractAddress]);
END;

IF NOT EXISTS (
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [blockchain].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [blockchain].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [blockchain].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260709110131_InitialBlockchain'
)
BEGIN
    INSERT INTO [blockchain].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260709110131_InitialBlockchain', N'10.0.9');
END;

COMMIT;
GO

