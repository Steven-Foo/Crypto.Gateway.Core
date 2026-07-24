# Testing the withdrawal flow on EC2 (Swagger, ~5 minutes)

This is the quick, click-through test of the money-out flow on the EC2 **staging** box, using **Swagger**. It
seeds a test balance, triggers a real TRON **Nile** withdrawal, and shows you where to see it confirm.

> First deploy staging per [`ec2-staging.md`](ec2-staging.md) — that covers the `appsettings.Local.json`
> secrets, applying the migrations, and running with `ASPNETCORE_ENVIRONMENT=Staging`. This doc is the *test*
> once it's running.

## What you'll do

`POST /dev/credit-balance` (give the test merchant a balance) → `POST /api/v1/withdraw` (Swagger auto-signs) →
watch it confirm on Nile and settle in the database. Two calls.

## Step 0 — fund the hot wallet (once)

The withdrawal actually sends USDT from the **hot wallet** you set in `Withdrawal:HotWallets:Tron:Address`
(Local config). That address must hold:
- **test TRX** — both to *activate* the account and to pay the transfer's energy/bandwidth
  (faucet: <https://nileex.io/join/getJoinPage>). Without TRX the broadcast fails with *"account does not
  exist"*.
- **test USDT** — the funds it will send out.

Verify with `POST https://nile.trongrid.io/wallet/getaccount` `{"address":"T…","visible":true}` — a `{}` reply
means it still needs TRX.

## Step 1 — open Swagger

Browse to `https://<your-ec2-host>/swagger`. In staging, "Try it out" **auto-signs** every `/api/v1/*` call with
the seeded test merchant's key, so you leave the three `X-` headers blank. (This needs `Merchant:DevSeed:ApiKey`
and `Merchant:DevSeed:SigningSecret` set in the EC2 `appsettings.Local.json`.)

## Step 2 — give the test merchant a balance

`POST /dev/credit-balance` (unauthenticated dev helper) with query params:

| param | value |
|---|---|
| `merchantCode` | `STAGINGMERCHANT` (the seeded merchant — `Merchant:DevSeed:MerchantCode`) |
| `amount` | `10` (display USDT) |

This posts the same double-entry a confirmed deposit would (`Dr TreasuryAsset / Cr MerchantLiability`), so the
merchant now has 10 USDT to withdraw. It moves no real money — it's ledger bookkeeping for the test.

## Step 3 — withdraw

`POST /api/v1/withdraw` (leave the `X-` headers blank — Swagger signs it). Body:

```jsonc
{
  "paymentMethod": "usdt",
  "transactionId": "wd-test-001",          // your idempotency key — change it each run
  "toAddress": "T…",                        // destination Nile T-address (the "user")
  "amount": 1,                              // display USDT; destination gets this in full
  "callbackUrl": "https://<your-ec2-host>/dev/callbacks"
}
```

The response returns `referenceNo` (the withdrawal id) and `status: "pending"`. Behind it the workers run
build → sign → broadcast → confirm; with the staging `Confirmations: 2` it settles within a few seconds.

## Step 4 — verify

- **Balance:** `GET /api/v1/balance` → drops by `amount + fee` (the fee is `Withdrawal:Policies:Tron:FeeBaseUnits`).
- **On-chain:** find the tx in `withdrawal.Withdrawal.TransactionHash` (Step 5), then open
  `https://nile.tronscan.org/#/transaction/<txHash>` — a real transfer of your `amount` to the destination.
- **Database (DBeaver → the EC2 MSSQL, TCP `1433`, the `sa`/app login):**

  ```sql
  SELECT Status, TransactionHash, Amount, Fee, DATALENGTH(SignedTransaction) AS SignedBytes
  FROM withdrawal.Withdrawal ORDER BY CreatedAt DESC;

  SELECT j.ReferenceType, a.AccountType, e.Debit, e.Credit
  FROM ledger.JournalEntry e
  JOIN ledger.Journal j ON j.Id = e.JournalId
  JOIN ledger.Account a ON a.Id = e.AccountId
  ORDER BY e.Seq;
  ```

  You should see the withdrawal `Confirmed` with the real `TransactionHash` and a persisted signed blob, and the
  ledger showing `WithdrawalReserve` then `WithdrawalSettle` (custody down by the amount that left the chain, fee
  to `FeeRevenue`, clearing back to 0).

## Optional — test the real deposit path too

To fund the balance from an actual on-chain deposit instead of `/dev/credit-balance`:
1. `POST /api/v1/deposit` → returns a deposit **address** + `referenceNo`.
2. `POST /dev/scan-cursor?chain=Tron&lookback=50` (so the scanner looks just behind the tip).
3. Send USDT to that address from a funded Nile account.
4. Within ~20s it's detected and (after `Confirmations`) credited — check `GET /api/v1/balance`.
5. Then withdraw as above.

## Troubleshooting

| Symptom | Cause → fix |
|---|---|
| Broadcast fails: *account does not exist* | Hot wallet has no TRX → send it test TRX (Step 0). |
| Broadcast fails: *balance is not sufficient* | Hot wallet has no/low test USDT → send it test USDT. |
| Withdraw returns *insufficient balance* | Ledger balance too low → `POST /dev/credit-balance` a larger amount. |
| `/api/v1/*` returns 401 | The seed merchant/keys aren't set → check `Merchant:DevSeed:*` + `Merchant:ApiCredentials`/`SigningSecrets` in the EC2 Local config. |
| Withdraw returns *duplicate*/same id | `transactionId` is the idempotency key — use a new one each run. |
| Stuck in `Broadcast`, never `Confirmed` | Tx reverted on-chain (ops case), or the confirmation worker can't reach Nile → check logs + the tx on the explorer. |

> All of `/dev/*`, Swagger, and the seed merchant exist **only** in the testnet tier (Development + Staging).
> Setting `ASPNETCORE_ENVIRONMENT=Production` removes them and disables the throwaway signer (§10).
