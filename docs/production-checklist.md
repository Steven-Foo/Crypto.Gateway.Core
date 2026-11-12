# Production checklist

What has to be set up outside the code before real money goes through. Written 2026-11-06 from a full re-check of
the code (1,290 tests passing, 0 failing, every route exercised, all three APIs booted in Production mode). Work top to
bottom; each step says why it matters.

Related: [`kms-go-live.md`](kms-go-live.md) (KMS keys + launch order), [`ec2-staging.md`](ec2-staging.md) (the
staging box), [`../db/README.md`](../db/README.md) (database scripts).

---

## 1. Secrets — all NEW, never reuse anything from staging or this chat

Every secret below must be freshly generated for production. Anything that was pasted in chat, used on staging,
or committed to git is burned.

| Setting | Which APIs | Rule |
|---|---|---|
| `Merchant:ApiCredentials:Peppers:1` (+ `CurrentHashVersion: 1`) | **all three, identical** | Hashes merchant API secrets. The admin BO creates them, the backend API checks them — different values break every merchant's API calls. 32+ random bytes, base64. |
| `Merchant:SigningSecrets:Keys:1` (+ `CurrentKeyVersion: 1`) | **all three, identical** | AES-256 key (32 random bytes, base64) sealing merchant signing secrets. Same reason as above. |
| `Identity:TwoFactor:Secrets:Keys:1` (+ `CurrentKeyVersion: 1`) | admin BO | AES-256 key for staff authenticator secrets. The API refuses to start without it. |
| `MerchantIdentity:TwoFactor:Secrets:Keys:1` (+ `CurrentKeyVersion: 1`) | merchant BO | AES-256 key for merchant-user authenticator secrets. A different key from the staff one. |
| `Db:ConnectionString` | all three | Log in as **`cpe_app`** (see §3), never `sa`. |
| `Redis:ConnectionString`, `Mongo:ConnectionString` | all three | With passwords; neither reachable from the internet. |
| `Chains:Tron:ApiKey` | backend API, admin BO | A NEW TronGrid key (the staging one was exposed). |
| `Compliance:MistTrack:ApiKey` | backend API, admin BO | **Rotate it in the MistTrack dashboard** — the current key was committed to git (removed 2026-11-06, still in history). |
| Cloudflare API token (`Cloudflare:*`) | admin BO | New token, scoped to the one zone. |

Generate each key in PowerShell with a cryptographic random source:
`$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)`
(works in Windows PowerShell 5.1 and PowerShell 7; prints a 44-character key).

Put secrets in environment variables on the server (`Merchant__SigningSecrets__Keys__1=...`) or a protected
`appsettings.Production.json` outside the repo — never in a tracked file.

## 2. Settings that differ from staging

| Setting | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` on all three. (Staging ran as Development — that is what made it use the repo's dev keys.) |
| `KeyManagement:Kms:Enabled`, `Region`, `KeyArns:Deposit`, `KeyArns:Withdrawal`, `KeyArns:Energy` | All three APIs. Each refuses to start if `Enabled` is true with the region or the Deposit/Withdrawal ARN missing. |
| `Chains:Tron:RpcBaseUrl` | `https://api.trongrid.io` (already the default — mainnet). |
| `Blockchain:Assets` | Already mainnet USDT `TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t`; must stay identical on all three APIs. |
| `Cors:AllowedOrigins` | Admin BO: the admin UI's exact origin. Merchant BO: the merchant UI's exact origin. Never `*`. |
| `Auth:Cookie:SameSite` | `Lax` if UI and API share a domain; `None` only for a cross-site split. |
| `Gateway:BaseUrl` | The public HTTPS URL of the backend API (it builds the payer's `payUrl`). |
| `Gateway:ClientIp:TrustedProxyRanges` | Cloudflare's current list (already set; refresh if Cloudflare changes it). |
| Screening switches | All OFF by default. Turn on deliberately: `Compliance:Enabled`, `Withdrawal:Screening:Enabled`, `Merchant:Screening:ScreenSettlementWallets`, `Merchant:Screening:RescreenSettlementWallets`, `Wallet:Screening:Enabled`, `Sweep:Screening:Enabled` (see `address-screening.md`). |
| `Merchant:DefaultFee` | Leave at 0/0 on all three; set default prices in the back office instead (they apply on every API). |

## 3. Database

1. Run `db/sql/00-bootstrap.sql` once as sysadmin, **passing both passwords** (it has no defaults and stops
   without them): see `db/README.md` Step 1.
2. Run the module scripts `10-…` to `150-…` in order (as `cpe_migrator` or sysadmin). `50-ledger.sql` must run
   after the bootstrap: it applies the "ledger rows can never be edited or deleted" protection to `cpe_app`.
3. The APIs connect as **`cpe_app`**. Never `sa` and never a `db_owner` — SQL Server ignores that protection
   for owners.
4. Run `db/mongo/00-bootstrap.js` against MongoDB (needs `mongosh`).
5. Automatic backups, and **one test restore** before launch.

All scripts were re-checked 2026-11-06: every migration present, and the whole set applies cleanly twice to an
empty database.

## 4. Server

- The backend API (MerchantGateway) runs as a **Windows service**, not under IIS — IIS stops idle apps, and with
  them the deposit scanner and every money worker. Set the service to **restart on failure**
  (`sc failure <name> reset= 86400 actions= restart/60000/restart/60000/restart/60000`) and, if SQL Server is
  on the same box, to **depend on it** (`sc config <name> depend= MSSQLSERVER`). If SQL Server is not ready yet
  the API stays up and retries each worker every few seconds (verified), so this is belt and braces.
- Admin BO and merchant BO can stay under IIS (they run no workers).
- HTTPS everywhere, behind Cloudflare. SQL Server, Redis and MongoDB closed to the internet.
- A reverse proxy must never log the `X-2FA-Code` header (see `ec2-staging.md` §9.6).
- Do not run the nightly-shutdown staging scripts on production.

## 5. AWS KMS

Follow `kms-go-live.md`: three customer-managed keys (deposit, withdrawal, energy), one instance role with
`kms:Encrypt` + `kms:Decrypt` on exactly those keys, then the validation step. On one shared server all three APIs
share that role; the admin BO and merchant BO are blocked from unsealing seeds in code.

## 6. First start, in this order

1. Start the three APIs; confirm each logs `Now listening` and nothing about a missing key.
2. **First admin:** on the server, `dotnet CryptoPaymentEngine.Api.OperationsApi.dll --bootstrap-admin <name>`
   (with the production environment variables set). Sign in, set up the authenticator.
3. Back office → 2FA policy: confirm the recommended actions are ticked (a fresh database ticks them all).
4. Register the cold **Safe** and **Danger** collection wallets (keys on hardware, never in the system).
5. **Hot pool:** Treasury → create hot pool. Fund each address from the company wallet; record each transfer
   as a top-up.
6. **Energy:** Energy → create staking wallet, send TRX to it, save the rules with auto-stake on. Size the TRX
   for about 15,000 TRX per address that may be waiting to sweep at the same time (mainnet rate, Nov 2026).
7. **Platform defaults:** back office → platform defaults: default fees, deposit/payout limits, approval threshold.
8. Merchant onboarding: IP allowlist (an empty list blocks every API call), approved settlement wallet, fees.

## 7. Before real merchants

- Small real-money mainnet test, end to end: deposit → sweep to cold → top-up → payout → cash-out → a rejection.
  Then confirm the custody audit (reconciliation) reads Balanced.
- Monitoring/alerts: ledger drift, low energy, low hot-wallet balances, stuck payouts, service down. The
  `/health` endpoints only say the process is up — they do not check the database.
- Security review / penetration test; written runbooks (stuck payout, drift, lost 2FA, suspected key leak,
  provider outage).

## Known, accepted limits (not blockers)

- If the scanner falls far behind (hours), it catches up 500 blocks per pass and checks every USDT transfer in
  them against our addresses one by one — roughly 2–3 minutes per hour of downtime. Fine for a launch.
- Energy take-back runs from one instance; two instances running it at once is harmless (the chain refuses the
  duplicate).
- Native TRX deposits, Ethereum and Solana are not built.
