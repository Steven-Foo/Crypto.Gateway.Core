# EC2 staging deployment (testnet tier)

Staging runs the **same code and the same money-out path as local**, on EC2, against the **TRON Nile testnet**
with a **throwaway signer**. It is a *testnet tier* — it is **not** production.

## The security model (read this first)

The host has three tiers, decided purely by `ASPNETCORE_ENVIRONMENT`:

| Tier | Signer / keys | Chain | Dev seed + Swagger + /dev |
|---|---|---|---|
| **Development** (local) | throwaway signer via `Withdrawal:LiveTron` | Nile | on |
| **Staging** (EC2) | throwaway signer via `Withdrawal:LiveTron` | Nile | on |
| **Production** | **none** unless `KeyManagement:Kms:Enabled` → the KMS-envelope signer (built; validate per [`kms-go-live.md`](kms-go-live.md)) | mainnet | **off** |

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

1. **Bring KMS custody live.** The KMS-envelope signer + provisioner are **built** — set
   `KeyManagement:Kms:Enabled` with the two CMK ARNs and validate per **[`kms-go-live.md`](kms-go-live.md)** (an
   EC2 instance role, no static keys). Enabling KMS is what registers the real signer + TRON tx-engine; until it
   is enabled, Production withdrawal/sweep stay inert by design — the throwaway signer is *not* carried over.
2. Set `ASPNETCORE_ENVIRONMENT=Production`. This alone disables the throwaway signer, the in-memory secret
   provider, dev seeding, Swagger, and `/dev/*` (fail-safe).
3. Point `Chains:Tron:RpcBaseUrl` at **mainnet** and set `Blockchain:Assets` USDT to the **mainnet** contract.
4. Provision **real merchants** through the Merchant module (no dev seed) and rotate the pepper/HMAC keys to
   production secrets held in a secret manager.
5. Deposit **address provisioning** switches to the KMS-backed provisioner automatically once
   `KeyManagement:Kms:Enabled` is set (step 1) — no separate action; the dev in-memory provisioner is never
   registered in Production.

Everything else — the connection-string mechanism, the config layering, the module wiring — stays identical, so
the transition is a change of *values and the signer*, not of code.

## 6. Running staging day to day

Five scripts in `tools/dev/` cover the box's routine. Copy them to the box (for example `C:\projects`) and run them
from an **elevated Windows PowerShell 5.1**. None prints a secret: connection strings, keys and peppers show as a
length and a short fingerprint, so output is safe to paste into a ticket or chat. Anything that changes state is a
dry run unless you pass `-Force`.

| Script | Run it when | What it does |
|---|---|---|
| `Start-StagingPlatform.ps1` | After every boot. Register once with `-RegisterStartupTask` and it runs itself. | Starts SQL Server, Redis, MongoDB and IIS and waits until each answers; sets every host's app pool to always-running; decides whether the deposit scanner catches up or skips ahead; starts the hosts and checks the scanner moves. Log in `boot-logs\`. |
| `Get-HostConfigReport.ps1` | After a deploy, or when one host behaves differently from another. | The effective config of each IIS site (which environment, which files, which value won), with shared secrets fingerprinted so you can see whether the hosts agree. |
| `Get-DepositDiagnosis.ps1 -Folder <gateway> -TxHash <hash>` | A deposit did not confirm. | Walks the path in order: node and key, the transaction (token, recipient, amount), scanner cursor, deposit row, outbox, invoice, host logs. Says which step stopped. |
| `Set-ScanCursor.ps1 -Folder <gateway>` | You need to move the scanner by hand: rescan a range, or skip a gap. | Shows the skipped range and the invoices at risk; with `-Force` stops the gateway, moves the cursor, restarts, and watches it move. `-FromTxHash` rescans from a given payment. |
| `Switch-StagingCustody.ps1 -Mode Kms` / `-Mode InMemory` | Testing KMS custody on testnet, and switching back. | See below. |

### The gateway must stay running

IIS starts an in-process ASP.NET Core app on its first request and stops it when the app pool idles (20 minutes by
default) or recycles. The gateway's background workers (deposit scanner, confirmation, outbox relay, invoice expiry,
withdrawal processing) stop with it, and nothing restarts them: payers talk to the pay page and the portal, not to
the gateway. In September 2026 this left staging with no scanner for five days, and a tester's payment never
confirmed. `Start-StagingPlatform.ps1` sets each host's pool to `AlwaysRunning` with no idle timeout and no
scheduled recycle, and turns preload on. Preload needs the IIS Application Initialization feature, installed once
with `Install-WindowsFeature Web-AppInit`. For production, run MerchantGateway as a Windows service instead.

### After a night or weekend switched off

The scanner reads 500 blocks every 10 seconds and TRON makes one block every 3 seconds, so a night off (~14,000
blocks) takes about 5 minutes to catch up and a weekend (~72,000) about 25. Catching up never misses a payment, so
`Start-StagingPlatform.ps1` leaves the cursor alone whenever the catch-up fits in `-MaxCatchUpMinutes` (default 60).

Only a longer gap is skipped, and never past a block where an invoice could have been paid. If an invoice was open
when the box went down, or was created since, scanning resumes from just before that moment, found by block
timestamp. Only if no invoice was payable does it jump to the tip. Each skip is appended to
`scan-cursor-skips.csv` next to the script. A transfer to one of our addresses with no open invoice inside a
skipped range is not detected; rescan that range with `Set-ScanCursor.ps1 -Block <first skipped block - 1> -Force`.

### KMS custody on staging

`KeyManagement:Kms:Enabled=true` makes the testnet tier use AWS KMS envelope custody, the same custody code
Production runs, while keeping the testnet conveniences (staging merchant seed, Swagger, `/dev` endpoints). Leave it
false and staging uses the in-memory store. A host registers exactly one of the two.

**Before the first switch:**

1. Create **two testnet-only CMKs** (deposit and withdrawal), never the production ones. Anyone who controls this
   box can decrypt whatever its instance role allows.
2. Attach an instance role that can use only those two keys: [`kms-go-live.md`](kms-go-live.md) steps 1 and 2.
   No `AWS_ACCESS_KEY_ID` anywhere on the box; the script refuses to run if one is set.
3. Prove encrypt and decrypt with [`kms-go-live.md`](kms-go-live.md) step 3.

**Switching:**

```powershell
.\Switch-StagingCustody.ps1 -Mode Kms -Region ap-southeast-1 -DepositKeyArn <arn> -WithdrawalKeyArn <arn>   # dry run
.\Switch-StagingCustody.ps1 -Mode Kms -Region ap-southeast-1 -DepositKeyArn <arn> -WithdrawalKeyArn <arn> -Force
.\Switch-StagingCustody.ps1 -Mode InMemory -Force                                                            # back
```

The script refuses a build without the switch, checks the ARNs and the instance role, shows which addresses become
receive-only, backs up and updates `appsettings.Local.json` on all three hosts, and starts the gateway first. If the
gateway does not come up cleanly, it restores the backups and restarts on the previous setting.

**What switching does.** Every wallet row records the store that holds its secret. On boot the gateway
(`CustodyModeReconciler`) archives the active wallets of the store that is off, and restores, per merchant, chain
and purpose, the wallet it archived there last time. A restored wallet keeps its derivation index, so no address
is ever issued twice. The other hosts only read the setting; the gateway owns the switch.

| | Effect |
|---|---|
| Deposits | Keep working. Detection needs no key, so every existing address still receives and credits. |
| Sweeps and payouts from existing addresses | Stop until you switch back: the key is in the store that is off. A sweep from such an address fails without moving anything; a payout with no signable hot wallet waits in `AwaitingFunds` with its reserve held. |
| Hot pool in KMS mode | A new KMS withdrawal wallet and pool are created on boot, **empty**. Fund them on Nile before testing a payout. |
| Merchant deposit wallets in KMS mode | Created under KMS on each merchant's next deposit address. |
| Energy staking in KMS mode | Inert: its seeder imports a private key from config, and there is no Energy CMK. |

Verified by booting the gateway build against a local development database: switching to KMS archived all six in-memory wallets, switching
back restored all six with identical derivation indexes, and KMS enabled without its ARNs refused to start and
changed nothing. Sealing a real seed under KMS is proven only on the box (step 3 above, then the first KMS boot).

## 7. Merchant API IP allowlist

Every signed call to the merchant API (`/api/v1/*`) must come from an address on that merchant's IP allowlist, or it
is refused with **403 "IP address not whitelisted."**. **An empty allowlist refuses every call.** The pay page and
the portal logins are not affected.

### Before deploying the build that enforces it

Any merchant listed by this query loses API access the moment the new gateway starts:

```sql
SELECT m.MerchantCode, m.Status, c.AllowedIpsCsv
FROM merchant.Merchant m
JOIN merchant.MerchantConfiguration c ON c.MerchantId = m.Id
WHERE m.Status <> 'Closed'
  AND (c.AllowedIpsCsv IS NULL OR c.AllowedIpsCsv LIKE '%/%');
```

- `AllowedIpsCsv` **NULL**: no list, so every call is refused. Add the merchant's server addresses first, in the
  portal (`PUT /api/v1/portal/allowed-ips`) or Ops (`PUT /api/v1/ops/merchants/{id}/allowed-ips`).
- **Contains `/`**: a CIDR range saved when the portal still accepted ranges. Ranges now match nothing; replace
  each with the servers' individual addresses.
- On staging this includes the test merchant: add the **public** address of every machine a tester signs requests
  from. Behind Cloudflare that is the address the internet sees, not a `10.x`/`192.168.x` LAN address.

### How the caller's address is decided

The gateway sits behind Cloudflare, so every connection arrives from a Cloudflare edge and the real caller is in
`CF-Connecting-IP`. That header is believed **only** on connections from the ranges in
`Gateway:ClientIp:TrustedProxyRanges` (`appsettings.json`, Cloudflare's published list fetched 2026-09-17). A
connection from anywhere else is judged by its own address, so a caller who reaches the EC2 box directly cannot
claim to be an allowlisted server.

- **Keep the range list current.** Cloudflare announces changes to https://www.cloudflare.com/ips/. A missing range
  shows up as 403s for callers routed through that edge, with the log naming a Cloudflare address as the caller.
- **Recommended, not required:** restrict the EC2 security group's HTTP/HTTPS ingress to the same ranges, so the
  origin is not reachable except through Cloudflare.
- **If a reverse proxy is added in front of the gateway on the box** (for example IIS ARR for the Windows-service
  plan), connections arrive from `127.0.0.1` and this logic must be revisited before that ships. Do not simply add
  loopback to the trusted ranges: a direct caller could then forge the header through the proxy.

### Diagnosing a 403

The gateway logs `Refused API call for merchant <code> from <address>: not on its IP allowlist (<n> entries).`. The
address there is the one to add. `0 entries` means the list is empty.

## 8. Treasury cold reload removed, and top-up verification on the Ops host

The in-system cold reload (build a cold-treasury→hot-pool transfer, sign it client-side, broadcast it from the
gateway) was removed on 2026-09-17. Funds reach the hot pool only as **top-ups**: finance sends company funds
outside the system, and an admin records the transfer, which is verified on-chain.

### Before deploying the build that removes it

That build's migration `RemoveTreasuryReload` **drops `treasury.TreasuryReload`**. Check nothing is still in flight:

```sql
SELECT * FROM treasury.TreasuryReload WHERE Status NOT IN ('Confirmed', 'Failed');
```

Any row returned is a reload the gateway was still tracking; after the drop nothing will. Confirm it on the
explorer first. To keep the history, copy it out before migrating:

```sql
SELECT * INTO dbo.TreasuryReload_Archive_20260917 FROM treasury.TreasuryReload;
```

### Top-up verification needs the Ops host to read the chain

Recording a top-up (and a merchant settlement) looks the transaction hash up on-chain from the **Ops** host. Outside
Development it always uses the real TRON node. In **Development** it uses the in-memory chain, which has never
seen a real Nile transaction and refuses every hash, unless `Chains:Tron:Live=true`. On a box where the Ops host
runs as Development, give it the same chain settings as the gateway, in its `appsettings.Local.json`:

```json
"Chains": { "Tron": { "Live": true, "RpcBaseUrl": "https://nile.trongrid.io", "ApiKey": "<TronGrid key>" } }
```

Before this build, top-up recording could not work at all under Staging or Production on the Ops host: no
verifier was registered outside its Development block.
