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

Apply in that order (`10` → `100`) — later modules only ever reference earlier ones by opaque
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

### Step 3 — lock down the ledger (after the ledger migration exists)

The ledger is append-only. Enforce it in the database, not just the app — see the commented
`DENY UPDATE, DELETE` block at the end of `sql/00-bootstrap.sql`.

---

## 2. MongoDB

```bash
mongosh "mongodb://localhost:27017" --file db/mongo/00-bootstrap.js
```

Idempotent: creates collections, JSON-Schema validators, and indexes (including TTL indexes for
`RpcLog`, `WebhookLog`, `WalletResourceHistory`).

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
