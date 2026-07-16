SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'[ledger].[__EFMigrationsHistory]') IS NULL
BEGIN
    IF SCHEMA_ID(N'ledger') IS NULL EXEC(N'CREATE SCHEMA [ledger];');
    CREATE TABLE [ledger].[__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    IF SCHEMA_ID(N'ledger') IS NULL EXEC(N'CREATE SCHEMA [ledger];');
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE TABLE [ledger].[Account] (
        [Id] uniqueidentifier NOT NULL,
        [AccountType] nvarchar(32) NOT NULL,
        [OwnerType] nvarchar(16) NOT NULL,
        [OwnerId] uniqueidentifier NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [NormalSide] nvarchar(8) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_Account] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE TABLE [ledger].[Journal] (
        [Id] uniqueidentifier NOT NULL,
        [ReferenceType] nvarchar(24) NOT NULL,
        [ReferenceId] uniqueidentifier NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [MerchantId] uniqueidentifier NULL,
        [Description] nvarchar(512) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_Journal] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE TABLE [ledger].[OutboxMessage] (
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
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE TABLE [ledger].[AccountBalance] (
        [Id] uniqueidentifier NOT NULL,
        [Balance] decimal(38,0) NOT NULL,
        [LastEntryId] uniqueidentifier NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [RowVersion] rowversion NULL,
        CONSTRAINT [PK_AccountBalance] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AccountBalance_NonNegative] CHECK ([Balance] >= 0),
        CONSTRAINT [FK_AccountBalance_Account_Id] FOREIGN KEY ([Id]) REFERENCES [ledger].[Account] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE TABLE [ledger].[JournalEntry] (
        [Id] uniqueidentifier NOT NULL,
        [JournalId] uniqueidentifier NOT NULL,
        [AccountId] uniqueidentifier NOT NULL,
        [AssetId] uniqueidentifier NOT NULL,
        [Debit] decimal(38,0) NOT NULL,
        [Credit] decimal(38,0) NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [Seq] bigint NOT NULL IDENTITY,
        CONSTRAINT [PK_JournalEntry] PRIMARY KEY NONCLUSTERED ([Id]),
        CONSTRAINT [CK_JournalEntry_DebitXorCredit] CHECK (([Debit] = 0 AND [Credit] > 0) OR ([Debit] > 0 AND [Credit] = 0)),
        CONSTRAINT [FK_JournalEntry_Journal_JournalId] FOREIGN KEY ([JournalId]) REFERENCES [ledger].[Journal] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Account_OwnerId_AssetId] ON [ledger].[Account] ([OwnerId], [AssetId]) WHERE [OwnerId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Account_Natural] ON [ledger].[Account] ([OwnerType], [OwnerId], [AssetId], [AccountType]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_Journal_Merchant_CreatedAt] ON [ledger].[Journal] ([MerchantId], [CreatedAt]) WHERE [MerchantId] IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_Journal_Seq] ON [ledger].[Journal] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Journal_Reference] ON [ledger].[Journal] ([ReferenceType], [ReferenceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE INDEX [IX_JournalEntry_AccountId] ON [ledger].[JournalEntry] ([AccountId]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE INDEX [IX_JournalEntry_JournalId] ON [ledger].[JournalEntry] ([JournalId]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_JournalEntry_Seq] ON [ledger].[JournalEntry] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    EXEC(N'CREATE INDEX [IX_OutboxMessage_ProcessedOnUtc] ON [ledger].[OutboxMessage] ([ProcessedOnUtc]) WHERE [ProcessedOnUtc] IS NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    CREATE UNIQUE CLUSTERED INDEX [IX_OutboxMessage_Seq] ON [ledger].[OutboxMessage] ([Seq]);
END;

IF NOT EXISTS (
    SELECT * FROM [ledger].[__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260713053814_InitialLedger'
)
BEGIN
    INSERT INTO [ledger].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260713053814_InitialLedger', N'10.0.9');
END;

COMMIT;
GO


-- ─────────────────────────────────────────────────────────────────────────────
-- Ledger immutability (§14). The ledger is append-only: once the tables exist,
-- deny the application role UPDATE/DELETE so even a buggy or compromised app can
-- never rewrite financial history. Corrections must be new compensating journals.
-- AccountBalance stays updatable — it is a rebuildable cache, not truth.
-- Guarded so it is a no-op where the app login is absent (e.g. dev on LocalDB).
-- ─────────────────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'cpe_app')
BEGIN
    DENY UPDATE, DELETE ON OBJECT::[ledger].[Journal]      TO [cpe_app];
    DENY UPDATE, DELETE ON OBJECT::[ledger].[JournalEntry] TO [cpe_app];
END;
GO
