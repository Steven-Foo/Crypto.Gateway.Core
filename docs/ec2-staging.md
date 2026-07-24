# EC2 staging deployment (testnet tier)

Staging runs the **same code and the same money-out path as local**, on EC2, against the **TRON Nile testnet**
with a **throwaway signer**. It is a *testnet tier* — it is **not** production.

## The security model (read this first)

The host has three tiers, decided purely by `ASPNETCORE_ENVIRONMENT`:

| Tier | Signer / keys | Chain | Dev seed + Swagger + /dev |
|---|---|---|---|
| **Development** (local) | throwaway signer via `Withdrawal:LiveTron` | Nile | on |
| **Staging** (EC2) | throwaway signer via `Withdrawal:LiveTron` | Nile | on |
| **Production** | **none** — a KMS-backed signer must be added first (not built yet) | mainnet | **off** |

`Development` and `Staging` are the **testnet tiers**. `Production` is a **hard wall**: it registers **no**
key-loading signer, no in-memory secret provider, and no dev seeding. So **flipping
`ASPNETCORE_ENVIRONMENT` from `Staging` to `Production` automatically disables the throwaway signer** —
withdrawal processing goes inert until a real KMS signer lands. This is the fail-safe that lets you promote the
same box to prod without ever accidentally moving real money with a testnet key (§10).

**Non-negotiables for staging:**
- The signing key is a **throwaway Nile key holding faucet funds only** — never a real key, never mainnet.
- **No secret is committed.** Everything sensitive lives in the git-ignored `appsettings.Local.json` on the box
  (or environment variables). This file is exactly the mechanism production will use — only the values (and the
  signer) change.
- **Lock the EC2 security group** to trusted IPs. Staging exposes Swagger and a seeded test merchant.
- The previously-leaked EC2 credentials must be **rotated** — never reuse them.

## Prerequisites

- The EC2 box already running **MSSQL + Redis + MongoDB** (native, as the deposit workflow already uses).
- A **throwaway funded Nile account** — see `docs/withdrawal-testnet.md` (fund it with faucet **TRX** to
  activate + pay gas, and hold some test TRC-20).
- The .NET 10 SDK/runtime on the box (or a published build).

## 1. Secrets + per-box config — `appsettings.Local.json` on the EC2 box (git-ignored)

Create this next to the host's `appsettings.json` **on EC2 only**. It is never committed.

```jsonc
{
  "Db":    { "ConnectionString": "Server=localhost,1433;Database=CryptoPaymentEngine;User Id=cpe_app;Password=<staging-pwd>;TrustServerCertificate=True" },
  "Redis": { "ConnectionString": "localhost:6379" },
  "Mongo": { "ConnectionString": "mongodb://localhost:27017", "Database": "CryptoPaymentEngine" },

  "Gateway": { "BaseUrl": "https://<ec2-host-or-domain>" },

  "Chains": { "Tron": { "ApiKey": "<TronGrid Nile API key>" } },

  "Merchant": {
    "ApiCredentials": { "Peppers": { "1": "<random staging pepper>" } },
    "SigningSecrets": { "Keys": { "1": "<base64 32-byte staging HMAC key>" } },
    "DevSeed": {
      "ApiKey":        "<staging test merchant api key>",
      "ApiSecret":     "<staging test merchant bearer secret>",
      "SigningSecret": "<staging test merchant 64-hex HMAC signing secret>"
    }
  },

  "Withdrawal": {
    "HotWallets": { "Tron": { "Address": "<throwaway T-address>", "KeyReference": "kms://tron/hot/0" } }
  },

  "KeyManagement": {
    "DevSecrets": {
      "kms://tron/hot/0": "<throwaway 64-hex private key>",
      "dev/tron/deposit/xpub": "<account xpub at m/44'/195'/0'/0 for deposit provisioning>"
    }
  }
}
```

Notes:
- Env vars work too for the simple keys (`Db__ConnectionString`, `Chains__Tron__ApiKey`,
  `Merchant__ApiCredentials__Peppers__1`, …) and are preferable if you have a secret manager. But
  `KeyManagement:DevSecrets` and `Withdrawal:HotWallets` are easiest in `Local.json` because the key reference
  `kms://tron/hot/0` isn't a valid environment-variable name (it contains `:` and `/`).
- Use the **`cpe_app`** login (DML only) for the runtime connection string; use **`cpe_migrator`** only for
  applying migrations (below). See `db/README.md`.

## 2. Apply database migrations to the EC2 MSSQL

The withdrawal schema gained a `SignedTransaction` column since the deposit workflow was set up
(`20260723103610_AddWithdrawalSignedTransaction`). Bring every module's schema up to date. Either:

```bash
# Option A — EF tooling (needs the SDK + the migrator login)
export CPE_DB_CONNECTION='Server=localhost,1433;Database=CryptoPaymentEngine;User Id=cpe_migrator;Password=<pwd>;TrustServerCertificate=True'
dotnet ef database update -p src/Gateway.Core/PaymentProcessing/Withdrawal/Infrastructure \
  -s src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway --context WithdrawalDbContext
# …repeat per module (see db/README.md table), or:

# Option B — the idempotent scripts (safe to re-run)
sqlcmd -S localhost,1433 -U cpe_migrator -P '<pwd>' -C -d CryptoPaymentEngine -i db/sql/70-withdrawal.sql
```

## 3. Run

```bash
ASPNETCORE_ENVIRONMENT=Staging dotnet run --project src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway
# (or run your published build with that environment variable)
```

On boot the host wires the **real** TRON build/sign/broadcast against Nile with the throwaway signer, seeds the
staging test merchant, and exposes Swagger at `/swagger`.

## 4. Test + verify

- Submit a signed `POST /api/v1/withdraw` (Swagger's "Try it out" auto-signs with the seed merchant, or use
  `tools/dev/Invoke-MerchantRequest.ps1`). The merchant needs a ledger balance first — credit one via a Nile
  deposit, or seed one as in the tests.
- Watch it go `Approved → Signing → Broadcast → Confirmed`; the ledger settles.
- **DBeaver → the EC2 MSSQL** (`<ec2-host>:1433`, `sa`/app login): inspect `withdrawal.Withdrawal` (real
  `TransactionHash` + persisted `SignedTransaction`) and the `ledger.*` journals/balances.
- Cross-check the tx on the Nile explorer: `https://nile.tronscan.org/#/transaction/<txid>`.

## 5. Staging → Production (the promotion checklist)

When this box becomes production, in order:

1. **Build + register a KMS/HSM-backed `ISigner`** (the real signer). Until it exists, Production withdrawal is
   inert by design — the throwaway signer is *not* carried over.
2. Set `ASPNETCORE_ENVIRONMENT=Production`. This alone disables the throwaway signer, the in-memory secret
   provider, dev seeding, Swagger, and `/dev/*` (fail-safe).
3. Point `Chains:Tron:RpcBaseUrl` at **mainnet** and set `Blockchain:Assets` USDT to the **mainnet** contract.
4. Provision **real merchants** through the Merchant module (no dev seed) and rotate the pepper/HMAC keys to
   production secrets held in a secret manager.
5. Replace the dev HD-wallet provisioner with the KMS-backed one for deposit address provisioning.

Everything else — the connection-string mechanism, the config layering, the module wiring — stays identical, so
the transition is a change of *values and the signer*, not of code.
