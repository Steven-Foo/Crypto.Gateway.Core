SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[energy].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'energy') IS NULL EXEC(N'CREATE SCHEMA [energy];');
    CREATE TABLE [energy].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    IF SCHEMA_ID(N'energy') IS NULL EXEC(N'CREATE SCHEMA [energy];');
END;

IF NOT EXISTS (
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    CREATE TABLE [energy].[EnergyPolicy] (
        [Id] uniqueidentifier NOT NULL,
        [Chain] nvarchar(16) NOT NULL,
        [WalletType] varchar(24) NOT NULL,
        [MinimumEnergy] decimal(38,0) NOT NULL,
        [TargetEnergy] decimal(38,0) NOT NULL,
        [StakeThreshold] decimal(38,0) NOT NULL,
        [RentalThreshold] decimal(38,0) NOT NULL,
        [EnableAutoStake] bit NOT NULL,
        [EnableAutoRent] bit NOT NULL,
        [IsEnabled] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_EnergyPolicy] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    CREATE TABLE [energy].[OutboxMessage] (
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
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    CREATE UNIQUE INDEX [IX_EnergyPolicy_Chain_WalletType] ON [energy].[EnergyPolicy] ([Chain], [WalletType]);
END;

IF NOT EXISTS (
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [energy].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [energy].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [energy].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260715082735_InitialEnergy'
)
BEGIN
    INSERT INTO [energy].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260715082735_InitialEnergy', N'10.0.9');
END;

COMMIT;
GO

