# Database Setup

Schema reference: [`docs/database-design.md`](../docs/database-design.md).

**EF Core migrations are the single source of truth for tables.** The scripts here create
what migrations *shouldn't* own (database, schemas, logins) and provide generated, runnable
DDL for environments where you can't run the EF tooling.

---

## 1. SQL Server

### Step 1 — bootstrap (once per environment, as sysadmin)

Edit the two passwords at the top of `sql/00-bootstrap.sql` first. **Never commit real passwords.**

```bash
# LocalDB (Windows dev)
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i db/sql/00-bootstrap.sql

# Any server
sqlcmd -S <server> -U sa -P '<password>' -i db/sql/00-bootstrap.sql
```

This creates:
- database `CryptoPaymentEngine`, with `READ_COMMITTED_SNAPSHOT ON` (readers don't block the ledger's writers);
- one schema per module — `blockchain`, `merchant`, `wallet`, `keymgmt`, `deposit`, `ledger`, `withdrawal`, `paymentintent`, `energy`, `sweep`, `platform`, `settlement`, `reconciliation`;
- two principals, deliberately separated:
  - **`cpe_migrator`** — DDL rights. Used only by `dotnet ef database update` / deploys.
  - **`cpe_app`** — DML only, no DDL. Used by the running application.

The split matters: a compromised application must not be able to drop the ledger.

The script is **idempotent** — safe to re-run.

### Step 2 — create tables

Point the tooling at your database and apply each module's migrations:

```bash
export CPE_DB_CONNECTION='Server=<server>;Database=CryptoPaymentEngine;User Id=cpe_migrator;Password=<pwd>;TrustServerCertificate=True'

dotnet ef database update \
  -p src/Gateway.Core/Blockchain/Infrastructure \
  -s src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway \
  --context BlockchainDbContext
```

Each module has its own `DbContext`, its own migrations, and its own `__EFMigrationsHistory`
table inside its own schema — so modules migrate independently. Every `-p` path is relative to
the repo root; `-s` is always the host above.

| Script | `--context` | `-p` (module Infrastructure project) |
|---|---|---|
| `10-blockchain.sql` | `BlockchainDbContext` | `src/Gateway.Core/Blockchain/Infrastructure` |
| `20-merchant.sql` | `MerchantDbContext` | `src/Gateway.Core/Merchant/Infrastructure` |
| `30-keymanagement.sql` | `KeyManagementDbContext` | `src/Gateway.Core/KeyManagement/Infrastructure` |
| `40-wallet.sql` | `WalletDbContext` | `src/Gateway.Core/AssetManagement/Wallet/Infrastructure` |
| `50-ledger.sql` | `LedgerDbContext` | `src/Gateway.Core/Financial/Ledger/Infrastructure` |
| `60-deposit.sql` | `DepositDbContext` | `src/Gateway.Core/PaymentProcessing/Deposit/Infrastructure` |
| `70-withdrawal.sql` | `WithdrawalDbContext` | `src/Gateway.Core/PaymentProcessing/Withdrawal/Infrastructure` |
| `80-paymentintent.sql` | `PaymentIntentDbContext` | `src/Gateway.Core/PaymentProcessing/PaymentIntent/Infrastructure` |
| `90-energy.sql` | `EnergyDbContext` | `src/Gateway.Core/AssetManagement/Energy/Infrastructure` |
| `100-identity.sql` | `IdentityDbContext` | `src/Gateway.Core/Platform/Identity/Infrastructure` |
| `110-notification.sql` | `NotificationDbContext` | `src/Gateway.Core/Platform/Notification/Infrastructure` |
| `120-sweep.sql` | `SweepDbContext` | `src/Gateway.Core/AssetManagement/Sweep/Infrastructure` |

Apply in that order (`10` → `120`) — later modules only ever reference earlier ones by opaque
`Guid`, never a cross-schema FK (§4.5), but keeping the numeric order matches how the modules
were built and is a reasonable default.

**Can't run the EF tooling?** Use the generated idempotent script instead:

```bash
sqlcmd -S <server> -d CryptoPaymentEngine -i db/sql/10-blockchain.sql
```

Regenerate it after changing a module's model (substitute that module's `--context` and `-p`
from the table above):

```bash
dotnet ef migrations script --idempotent \
  -p src/Gateway.Core/Blockchain/Infrastructure \
  -s src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway \
  --context BlockchainDbContext -o db/sql/10-blockchain.sql
```

**`50-ledger.sql` carries a hand-appended block** (the append-only `DENY UPDATE, DELETE` guard,
§14/Step 3 below) that `dotnet ef migrations script` does not know about and will silently drop
on regeneration. After regenerating that one file, re-append the block from git history (or from
`00-bootstrap.sql`'s commented Step 4, which documents the same DENY statements).

> ⚠️ **Add a migration → regenerate its script, in the same commit.** These scripts do not update
> themselves. When they drift, the failure is nasty and remote: a teammate builds a fresh database
> from `db/sql`, the tables look fine, and the app then dies at runtime on `Invalid column name '…'`
> — the dev merchant fails to seed and every signed `/api/v1` call returns *"Invalid API
> credentials."* (This has already happened once: `AddMerchantAllowedIps`,
> `AddDepositsReceivedCount`, and `AddPaymentIntentGracePeriod` shipped without their scripts.)
>
> Check for drift before you push — every migration on disk must appear in its script:
>
> ```bash
> # example: Merchant. Prints any migration missing from the generated script.
> for m in $(ls src/Gateway.Core/Merchant/Infrastructure/Persistence/Migrations/*.cs \
>              | grep -vE 'Designer|ModelSnapshot' | xargs -n1 basename | sed 's/\.cs$//'); do
>   grep -q "$m" db/sql/20-merchant.sql || echo "MISSING from 20-merchant.sql: $m"
> done
> ```
>
> Every generated script must also keep the `SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; GO`
> header at the top. `dotnet ef` does not emit it, and without it `sqlcmd` fails on any table
> carrying a **filtered index** (`UX_PaymentIntent_LiveWallet`, `IX_Deposit_Chain_Status`,
> `IX_HdWallet_MerchantId_Chain_Purpose`). Re-add it after regenerating.

### Step 3 — lock down the ledger (after the ledger migration exists)

The ledger is append-only. Enforce it in the database, not just the app — see the commented
`DENY UPDATE, DELETE` block at the end of `sql/00-bootstrap.sql`.

---

## 2. MongoDB

**Run MongoDB natively (a local Windows service), not in Docker.** Local dev and the local staging setup
both target `mongodb://localhost:27017` directly. The `mongodb` service in `docker-compose.yml` remains only
as a fallback for a machine with no native install — do not run both, they contend for port 27017 (see the
troubleshooting note below, which is exactly how that failure presents).

```bash
mongosh "mongodb://localhost:27017" --file db/mongo/00-bootstrap.js
```

Idempotent: creates collections, JSON-Schema validators, and indexes (including TTL indexes for
`RpcLog`, `WebhookLog`, `ResourceHistory`).

> **`mongosh` is a separate download.** The MongoDB *Server* MSI does not install it, and neither does
> Compass — so on a fresh machine this command will not exist even though the server is running. Install the
> MongoDB Shell separately. It lands in `%LOCALAPPDATA%\Programs\mongosh\` and is **not added to `PATH`**, so
> either add it or call it by full path. Nothing depends on it at runtime: the collections auto-create on
> first write; the bootstrap only adds the validators and indexes.

Re-running the bootstrap over populated collections is safe — it `collMod`s the validator onto the existing
collection rather than recreating it.

### Running the tests against it

Integration tests follow one convention for every backing store: **local by default, overridable by
environment.** Nothing spins up its own container.

| Store | Default | Override |
|---|---|---|
| SQL Server | `(localdb)\MSSQLLocalDB` | `CPE_TEST_SQL` (`{db}` is substituted per test database) |
| MongoDB | `mongodb://localhost:27017` | `CPE_TEST_MONGO` |

Mongo tests use their own database (`cpe_test_energy`), dropped before and after each run, so they never
touch `CryptoPaymentEngine`. If nothing is listening they **skip** rather than fail, and they give up in
~3s rather than the driver's default 30s.

### Version + startup troubleshooting (Windows)

Verified working: **MongoDB 8.2** (`8.2.12`). **MongoDB 8.3 does not run on Windows 10 (19045)** — `mongod.exe`
fails to load with exit code `0xC0000139` (`STATUS_ENTRYPOINT_NOT_FOUND`), so the service times out after 30s
*without ever writing a log file* (System event 7000/7009, and MSI Error 1920 during install). If mongod
produces no log at all, that is this, not a config problem — it is not AVX (that is `0xC000001D`), not folder
ACLs, and not a missing VC++ redistributable. Install 8.2 or another LTS.

If the service starts but immediately stops, and the log's last error is **`Error setting up transport
layer`**, something else already holds port 27017 — most often a leftover `cpe-mongodb` Docker container.
Stop it (`docker compose down`) and start the service again.

Start the service from an **elevated** shell (`net start MongoDB`, or `Start-Service MongoDB`); a
non-elevated attempt fails with `Cannot open MongoDB service on computer '.'`. Its data and log live under
`C:\Program Files\MongoDB\Server\<version>\{data,log}`, which only the service account can write — so a
foreground `mongod --dbpath ...` run as your own user needs a directory you own.

### Drift check — this script must match the code that writes these collections

Same trap as the `db/sql` drift documented in §1, and it fails just as silently. **A JSON-Schema
validator that disagrees with the document a store actually writes rejects every write** with a bare
`Document failed validation`, and the only visible symptom is a feature that quietly stops recording.

Three rules, all learned the hard way (2026-08-26):

- **The database name must match `Mongo:Database` in every host's `appsettings`** (all three hosts and
  `docker-compose.yml` say `CryptoPaymentEngine`). MongoDB forbids two databases whose names differ only
  by case, so a casing mismatch is not cosmetic: this script runs first (it is mounted into
  `docker-entrypoint-initdb.d`), wins the name, and the app's first write then dies with
  `db already exists with different case`.
- **A collection declared here must be one a store actually writes, under the same name and shape.**
  A collection declared under a name nothing uses is worse than useless — the app auto-creates the real
  one *without* the indexes declared here, so a TTL you believe is in place is not (this is exactly how
  `WalletResourceHistory` was declared while the code wrote `ResourceHistory`, leaving history to grow
  forever).
- **A `DateTimeOffset` must be mapped explicitly, or it is not a BSON date.** The driver serialises a bare
  `DateTimeOffset` as a nested `{ DateTime, Ticks, Offset }` **document**. That fails any
  `bsonType: "date"` declaration here — and, worse, a **TTL index on such a field expires nothing at all**,
  so a collection you believe is self-trimming grows forever with no error anywhere. Every `DateTimeOffset`
  written to Mongo therefore carries `[BsonRepresentation(BsonType.DateTime)]` (see `ResourceDocuments.cs`
  and `ReconciliationDocuments.cs`). This was live and rejecting **every** Energy resource and
  Reconciliation snapshot write until it was found. Values are UTC observations, so no offset is lost.

The writers, as of this file: `WalletResource` + `ResourceHistory` (Energy 5a `MongoWalletResourceStore` /
`MongoResourceHistoryStore`), `Reconciliation` + `ReconciliationHistory` (`MongoReconciliationStore` /
`MongoReconciliationHistoryStore`). `EnergyDelegation` is **superseded** — 5b moved staking/delegation into
the SQL `energy.EnergyOperation` aggregate. The remaining collections (`Block`, `BlockchainTransaction`,
`TransactionReceipt`, `ContractEvent`, `AddressMetadata`, `WalletSnapshot`, `RpcLog`, `WebhookLog`) are
declared ahead of use — **nothing writes them yet**; treat their shapes as proposals, not contracts.

Two invariants the validators enforce structurally:

1. **Mongo is never a source of truth for money.** It stores external/blockchain state. Balances
   are derived from the SQL ledger, never reconstructed from here.
2. **Amounts are base-unit strings, never `double`.** BSON doubles are IEEE-754 binary floats and
   cannot represent 1 wei exactly. The validators reject numeric amount fields (`^[0-9]{1,78}$`).

No mnemonic, seed, private key, or secret is ever written to MongoDB.

---

## 3. Configuration

The app reads these (see `CLAUDE.md` §3):

| Key | Notes |
|---|---|
| `Db:ConnectionString` | use `cpe_app`, **not** `cpe_migrator` |
| `Mongo:ConnectionString` | |
| `Redis:ConnectionString` | |
| `Chains:<Chain>:RpcUrl` | |
| `Chains:<Chain>:Confirmations` | |
| `Merchant:ApiCredentials:CurrentHashVersion` | pepper version used for **new** credential hashes |
| `Merchant:ApiCredentials:Peppers:<version>` | **secret** — load from KMS/secret store, never commit |

### API-credential peppers

Merchant API secrets are stored as `HMAC-SHA256(pepper, secret)`. The pepper is a server-side
secret held **outside** the database — it is what protects the credential table if it is ever
exfiltrated. The module refuses to start if no pepper exists for `CurrentHashVersion`.

```jsonc
// appsettings — peppers belong in a secret store, this shows the shape only
"Merchant": {
  "ApiCredentials": {
    "CurrentHashVersion": 2,
    "Peppers": { "1": "<old-pepper>", "2": "<current-pepper>" }
  }
}
```

**Rotating a pepper:** add the new version, bump `CurrentHashVersion`, and keep the old entry.
New credentials hash with the new pepper; existing ones keep verifying against the old one
(that's what `MerchantApiCredential.HashVersion` records). Remove the old pepper only once every
credential issued under it has been rotated out.

`CPE_DB_CONNECTION` is a **design-time-only** override used by `dotnet ef`; it is never read at runtime.

> `InvariantGlobalization` must stay `false` (see `Directory.Build.props`) —
> `Microsoft.Data.SqlClient` throws *"Globalization Invariant Mode is not supported"* when opening
> a connection under it.
