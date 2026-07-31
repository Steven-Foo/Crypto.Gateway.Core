SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[notification].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'notification') IS NULL EXEC(N'CREATE SCHEMA [notification];');
    CREATE TABLE [notification].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    IF SCHEMA_ID(N'notification') IS NULL EXEC(N'CREATE SCHEMA [notification];');
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    CREATE TABLE [notification].[CallbackDelivery] (
        [Id] uniqueidentifier NOT NULL,
        [ReferenceType] nvarchar(16) NOT NULL,
        [ReferenceId] uniqueidentifier NOT NULL,
        [CallbackUrl] nvarchar(2048) NOT NULL,
        [Body] nvarchar(max) NOT NULL,
        [CallbackType] varchar(64) NOT NULL,
        [Timestamp] varchar(32) NOT NULL,
        [SignatureHex] varchar(256) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [AttemptCount] int NOT NULL,
        [NextAttemptAt] datetimeoffset NULL,
        [FirstAttemptedAt] datetimeoffset NULL,
        [LastAttemptedAt] datetimeoffset NULL,
        [DeliveredAt] datetimeoffset NULL,
        [LastError] nvarchar(512) NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_CallbackDelivery] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    CREATE TABLE [notification].[OutboxMessage] (
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
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_CallbackDelivery_Seq] ON [notification].[CallbackDelivery] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    CREATE INDEX [IX_CallbackDelivery_Status_NextAttemptAt] ON [notification].[CallbackDelivery] ([Status], [NextAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    CREATE UNIQUE INDEX [UX_CallbackDelivery_Reference] ON [notification].[CallbackDelivery] ([ReferenceType], [ReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [notification].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [notification].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [notification].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260729102710_InitialNotification'
)
BEGIN
    INSERT INTO [notification].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260729102710_InitialNotification', N'10.0.9');
END;

COMMIT;
GO

