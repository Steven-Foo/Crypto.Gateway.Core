# Human-testable deposit on real TRON mainnet (local)

This reproduces the legacy PoC's **human-tested deposit flow** on our modular spine: create an invoice → open
the reused pay page → send real USDT-TRC20 → watch it get detected, credited to the immutable ledger (with the
fee split), matched to the invoice, and the **signed merchant callback fire** — all reviewable in DBeaver.

> ⚠️ **Real money, small amounts only.** Withdrawal *signing* is still deferred (§10), so the app cannot yet
> sweep funds back out. Use **dust** (e.g. 0.5–1 USDT). And see "Fund recovery" below before you send anything.

## Prerequisites
- **Docker Desktop** running.
- **.NET 10 SDK** + `dotnet-ef` (`dotnet tool install --global dotnet-ef`).
- **DBeaver** (or any SQL client).
- A **TRON wallet** you control (TronLink, etc.) with a little USDT-TRC20 + a few TRX for gas.
- A **fresh TronGrid API key** — https://www.trongrid.io/. ⚠️ **Never** the leaked `3a26aa49…` key.

## 1 · Bring up the backing services
```bash
docker compose up -d          # SQL Server :1433, Redis :6379, Mongo :27017
./tools/dev/Setup-LocalEnv.ps1   # creates the DB + applies every module's migrations
```
`docker compose up` also runs the Mongo bootstrap on first init. Redis is what lets the outbox dispatch the
callback — without it, detection credits the ledger but the callback never fires.

## 2 · Configure your secrets (git-ignored)
Edit **`src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway/appsettings.Local.json`** (already
created, never committed):

```jsonc
"Chains": { "Tron": { "Live": true, "ApiKey": "<YOUR fresh TronGrid key>" } },
"KeyManagement": { "DevMerchantXpub": "<YOUR account xpub at m/44'/195'/0'/0>" }
```

- **`Live: true` + `ApiKey`** switches the dev host from the in-memory chain to real mainnet detection.
- **`DevMerchantXpub`** — paste your own wallet's account xpub (the **change-level** key, `m/44'/195'/0'/0`).
  Deposit addresses then derive into **your** wallet so test funds are recoverable there. If you leave it
  empty, addresses come from a throwaway seed whose salt is **public in this repo** — fine for testing the
  plumbing, but **never send real funds** to it.

## 3 · Run the host
```bash
dotnet run --project src/Api/MerchantGateway/CryptoPaymentEngine.Api.MerchantGateway
```
Dev URL: `http://localhost:51079` (https `51078`). Swagger (API docs): `http://localhost:51079/swagger`.

## 4 · Point the scanner at "now"
On a fresh DB the scanner would cold-start at block 1 and crawl from genesis. Seed the cursor near the tip
**once**, after the host is up:
```bash
curl -X POST "http://localhost:51079/dev/scan-cursor?chain=Tron"
# → { "chain":"Tron", "tip":<current>, "cursorSetTo":<tip-20> }
```

## 5 · Create the deposit invoice
Use the signing helper (Swagger can't sign — see below):
```powershell
./tools/dev/Invoke-MerchantRequest.ps1 -BaseUrl http://localhost:51079 `
  -Path /api/v1/deposit -Body '{ "paymentMethod":"USDT", "transactionId":"mainnet-test-1", "userId":"u1", "expectedAmount":"1.00", "callbackUrl":null }'
```
The response has `address`, `referenceNo`, and `payUrl`.

## 6 · Open the pay page and pay
Open the `payUrl` (`http://localhost:51079/pay/{referenceNo}`) — the reused pay page shows the amount, a QR of
the address, and a countdown.

> ✅ **Safety check:** confirm the shown address appears in **your** wallet (it should, if you set
> `DevMerchantXpub`). If it doesn't, **stop** — don't send funds you can't recover.

Send the dust USDT-TRC20 to that address from your wallet.

## 7 · Watch it happen
Within a scan/confirmation cycle (a few blocks; `Deposit:Policies:Tron:Confirmations`):
- The **pay page** flips to **"Payment Received."**
- **`GET http://localhost:51079/dev/callbacks`** shows the signed callback the platform POSTed (headers +
  body) — the "callback handled on detection" you asked for.
- **DBeaver** shows the money move (below).

## Review in DBeaver
Connect: host `localhost` · port `1433` · user `sa` · password `Cpe_Dev_Passw0rd!` · database
`CryptoPaymentEngine` · **enable "Trust server certificate."**

| What | Where |
|---|---|
| The detected deposit | `deposit.Deposit` (Status `Confirmed`, your TxHash) |
| The immutable double-entry credit + **fee split** | `ledger.Journal` + `ledger.JournalEntry` (Dr TreasuryAsset / Cr MerchantLiability(net) / Cr FeeRevenue(fee)) |
| The merchant's derived balance | `ledger.AccountBalance` (join `ledger.Account` where `AccountType='MerchantLiability'`) |
| The invoice match | `paymentintent.PaymentIntent` (Status `Matched`) |
| The deposit address ↔ merchant | `wallet.Wallet` + per-merchant HD wallet in `keymgmt.HdWallet` |

## Swagger and the signature
`/swagger` documents the API, but its **"Try it out" cannot sign** requests (the merchant API is HMAC-signed:
`X-Api-Key` + `X-Timestamp` + `X-Signature` over `"{timestamp}\n{body}"`). Make real signed calls with
`tools/dev/Invoke-MerchantRequest.ps1`.

## Fund recovery
- With **`DevMerchantXpub` set**, deposit addresses are in your own wallet — recover/sweep with your wallet.
- With it **empty**, addresses derive from the public repo salt — recoverable only via a manual script from
  that salt + the merchant id + index. Don't send real funds to those.

## Known live-adapter caveats (first mainnet exercise)
The TRON JSON-RPC adapter was fixture-tested but its live round-trip was deferred to staging — this is that
first live run. Expect to tune: the **scan start** (step 4), **TronGrid rate limits** (a paid key helps),
**confirmations** (`Deposit:Policies:Tron`), and **native-TRX vs TRC-20** (only TRC-20 USDT is detected).
Withdrawal remains inert in dev (no real signer, §10).
