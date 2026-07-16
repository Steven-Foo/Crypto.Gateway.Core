# Dev deposit round-trip

How to drive a full **signed `/api/v1/deposit` → provisioned address → pay page** on a fresh clone, with no
manual database seeding. Everything below is **Development-only** and never runs in production (§10).

## What's wired for you (Development branch only)

| Piece | What it does | Config |
|---|---|---|
| `AddDevelopmentKeyCustody` | In-memory `ISecretProvider` (**public xpub only**) + seeds a TRON deposit HD wallet | `KeyManagement:DevWallets` / `DevSecrets` |
| `AddDevelopmentMerchantSeed` | Seeds one **active** test merchant with fixed, documented credentials | `Merchant:DevSeed` |

Both are idempotent (safe to restart) and both degrade to a warning — never a crash — if a schema isn't migrated.

## One-time setup

1. **Apply migrations** to the dev database (`(localdb)\MSSQLLocalDB` → `CryptoPaymentEngine`). The host does
   **not** auto-migrate. For each module (`Merchant`, `KeyManagement`, `AssetManagement/Wallet`,
   `PaymentProcessing/PaymentIntent`, `Financial/Ledger`, `PaymentProcessing/Deposit`,
   `PaymentProcessing/Withdrawal`):

   ```bash
   dotnet ef database update \
     --project src/Gateway.Core/<Module>/Infrastructure/<csproj> \
     --startup-project src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway.csproj \
     --context <ModuleDbContext>
   ```

   > Use the **host** as `--startup-project` — a class library as startup fails with
   > "Could not load System.Runtime 10.0.0.0". Override the target DB with `CPE_DB_CONNECTION` if needed.

2. **Run the host** (Development is the default profile):

   ```bash
   dotnet run --project src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway
   ```

   On startup you'll see `Seeded development merchant 'DEVMERCHANT' ...` and
   `Seeded development HD wallet 'TRON deposit pool (dev)' ...`. Note the port from `Now listening on: http://localhost:NNNNN`.

## The seeded dev credentials

From `appsettings.Development.json` (override in a git-ignored `appsettings.Local.json`):

- **X-Api-Key**: `cpe_dev_merchant`
- **SigningSecret** (64-hex HMAC key): `0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef`

The request signature is `HMAC-SHA256(hexDecode(SigningSecret), "{X-Timestamp}\n{body}")`, hex-encoded, sent as
`X-Signature`. `X-Timestamp` is Unix seconds and must be within 5 minutes.

## Sign & send

```powershell
./tools/dev/Invoke-MerchantRequest.ps1
```

That signs and POSTs `/api/v1/deposit` with the dev merchant and a sample USDT-TRON body. Expected response:

```json
{
  "isSuccess": true,
  "data": {
    "referenceNo": "…",
    "address": "T…",
    "chainType": "TRC",
    "payUrl": "…/pay/…"
  }
}
```

The `address` is now **per-merchant**: each merchant has its own HD wallet (separate seed), created on its
first deposit, so the dev merchant gets *its own* deterministic TRON address (derived from a fixed dev salt +
the merchant id — reproducible across restarts, but no longer the shared `TUEZSdK…` test vector, which
belonged to the old single-pool design). Capture the address from the response rather than asserting a fixed
value. The `payUrl`'s expected amount is the requested amount **grossed up by the deposit fee** (payer pays on
top) when the merchant has a fee schedule; with no schedule it equals the requested amount. Then check the pay
page: `GET /pay/{referenceNo}/info`.

Other endpoints:

```powershell
./tools/dev/Invoke-MerchantRequest.ps1 -Path '/api/v1/balance' -Body '{}'
```

## Reproducing production addresses locally

Paste a real branch xpub into a git-ignored `appsettings.Local.json` (never committed) and restart:

```json
{ "KeyManagement": { "DevSecrets": { "dev/tron/deposit/xpub": "<real branch xpub>" } } }
```

Provisioning is **watch-only** — it reads only the public xpub, never a seed — so this is safe. `appsettings.Local.json`
overrides any key and is git-ignored.
