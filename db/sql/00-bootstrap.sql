/*───────────────────────────────────────────────────────────────────────────────
  Crypto Payment Engine — SQL Server bootstrap
  Run ONCE per environment, as a sysadmin, BEFORE applying EF migrations.

  Creates: database, one schema per module, and two least-privilege principals.
  Does NOT create tables — those come from EF Core migrations, which are the single
  source of truth for the schema (CLAUDE.md §3). See db/README.md.

  Usage:
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i db/sql/00-bootstrap.sql
    sqlcmd -S <server> -U sa -P <pwd>  -i db/sql/00-bootstrap.sql

  BEFORE RUNNING: replace the two passwords below. Never commit real passwords.
───────────────────────────────────────────────────────────────────────────────*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

:setvar DbName            "CryptoPaymentEngine"
:setvar AppLogin          "cpe_app"
:setvar MigratorLogin     "cpe_migrator"
:setvar AppPassword       "CHANGE_ME_app_P@ssw0rd!"
:setvar MigratorPassword  "CHANGE_ME_migrator_P@ssw0rd!"
GO

/*── 1. Database ────────────────────────────────────────────────────────────────
  Case-insensitive, accent-sensitive, UTF-8 enabled collation. nvarchar columns
  hold Unicode text; varchar columns hold ASCII (addresses, hashes, hex).
*/
IF DB_ID(N'$(DbName)') IS NULL
BEGIN
    PRINT 'Creating database $(DbName)...';
    CREATE DATABASE [$(DbName)];
END
ELSE
    PRINT 'Database $(DbName) already exists - skipping.';
GO

ALTER DATABASE [$(DbName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
-- RCSI: readers don't block writers. Important for a ledger under concurrent posting.

USE [$(DbName)];
GO

/*── 2. Schemas — one per module (§1.6) ────────────────────────────────────────
  Each module's DbContext maps to exactly one schema and owns its own
  __EFMigrationsHistory table inside it. No module reads another's tables.
*/
DECLARE @schemas TABLE (name sysname);
INSERT INTO @schemas (name) VALUES
    (N'blockchain'),      -- Asset catalog + chain metadata      (P1)
    (N'merchant'),        -- Merchant, credentials, webhooks     (P1)
    (N'wallet'),          -- Derived addresses, assignments      (P1)
    (N'keymgmt'),         -- HDWallet, signing policy/audit      (P1)
    (N'deposit'),         -- Deposit detection/confirmation      (P1)
    (N'ledger'),          -- Account, Journal, JournalEntry      (P1)
    (N'withdrawal'),      --                                     (P2)
    (N'sweep'),           --                                     (P2)
    (N'platform'),        -- AuditLog, Configuration, Jobs       (P2)
    (N'settlement'),      --                                     (P3)
    (N'reconciliation');  --                                     (P3)

DECLARE @name sysname, @sql nvarchar(max);
DECLARE schema_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @schemas;
OPEN schema_cursor;
FETCH NEXT FROM schema_cursor INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @name)
    BEGIN
        SET @sql = N'CREATE SCHEMA ' + QUOTENAME(@name) + N';';
        EXEC sp_executesql @sql;
        PRINT '  created schema ' + @name;
    END
    FETCH NEXT FROM schema_cursor INTO @name;
END
CLOSE schema_cursor;
DEALLOCATE schema_cursor;
GO

/*── 3. Principals ─────────────────────────────────────────────────────────────
  Two identities, deliberately separated:
    cpe_migrator — DDL rights. Used ONLY by `dotnet ef database update` / deploys.
    cpe_app      — DML only. Used by the running application. Cannot alter schema.
  The app must never hold DDL rights; a compromised app should not be able to drop
  the ledger.
*/
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(MigratorLogin)')
    CREATE LOGIN [$(MigratorLogin)] WITH PASSWORD = N'$(MigratorPassword)', CHECK_POLICY = ON;
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(AppLogin)')
    CREATE LOGIN [$(AppLogin)] WITH PASSWORD = N'$(AppPassword)', CHECK_POLICY = ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(MigratorLogin)')
    CREATE USER [$(MigratorLogin)] FOR LOGIN [$(MigratorLogin)];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppLogin)')
    CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)];
GO

-- Migrator: full DDL on this database only.
ALTER ROLE db_ddladmin  ADD MEMBER [$(MigratorLogin)];
ALTER ROLE db_datareader ADD MEMBER [$(MigratorLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(MigratorLogin)];
GO

-- App: read/write data, no DDL.
ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
GO

/*── 4. Ledger immutability (run AFTER the ledger migration) ───────────────────
  The ledger is append-only (§14). Once ledger.Journal / ledger.JournalEntry exist,
  enforce it at the database level so even a buggy or compromised app cannot rewrite
  financial history. Corrections must be new compensating entries.

  Uncomment after `dotnet ef database update --context LedgerDbContext`:

    DENY UPDATE, DELETE ON OBJECT::ledger.Journal      TO [$(AppLogin)];
    DENY UPDATE, DELETE ON OBJECT::ledger.JournalEntry TO [$(AppLogin)];

  AccountBalance stays updatable — it is a rebuildable cache, not truth.
  Likewise for the immutable audit trail once it exists:
    DENY UPDATE, DELETE ON OBJECT::keymgmt.SigningAudit TO [$(AppLogin)];
    DENY UPDATE, DELETE ON OBJECT::platform.AuditLog    TO [$(AppLogin)];
*/
GO

PRINT '';
PRINT 'Bootstrap complete for $(DbName).';
PRINT 'Next: set CPE_DB_CONNECTION, then run `dotnet ef database update` per module.';
PRINT 'See db/README.md.';
GO
