# Withdrawal on TRON Nile testnet (Level 3)

This is the runbook for proving the money-out path **end to end against a real TRON node**, using the same
adapters production will use — the real transaction builder, a real secp256k1 signer, and the real
broadcaster — on the **Nile testnet** with faucet-only funds.

> **Security (§10).** The signer here loads a private key into the process, so it is wired **only** in
> Development behind an explicit flag, and only ever with a **throwaway testnet key holding faucet funds**.
> Never put a real key anywhere in this flow, never point it at mainnet, and keep the key in the git-ignored
> `appsettings.Local.json` / environment — never in a committed file. In production the key never enters the
> process at all: a KMS/HSM-backed signer replaces `TronSigner` behind the same `ISigner` port (not built yet,
> so production withdrawal stays inert by design).

## What is already proven without a node

`WithdrawalTronTestnetFlowTests` (in the Withdrawal test project) runs in CI with no node and no funds. It
drives the **real** builder + **real** signer (a genuine signature over a generated throwaway key) + **real**
broadcaster against an in-process canonical-Nile stub, on a real SQL Server, and asserts the whole database
state: reserve → build → sign (blob persisted) → broadcast → confirm → settle, with the ledger balances moving
exactly as they must. It also proves a re-processed withdrawal **re-broadcasts the same signed blob** (same tx
id) and never double-sends.

The live run below adds the one thing a stub can't: proof that a **real Nile node accepts our signature** and
the transaction confirms on-chain.

## Prerequisites for a live run

1. **A throwaway Nile account.** Generate a key pair (any TRON tool, e.g. TronLink set to Nile, or `tronweb`).
   Keep the **private key (64-hex)** and its **`T…` address**. This account is disposable.
2. **Fund it from the Nile faucet:** <https://nileex.io/join/getJoinPage> — get test **TRX** (pays the
   energy/bandwidth for the transfer).
3. **Hold some test TRC-20** at that address to transfer. Use a Nile TRC-20 token contract you can obtain test
   balance for (a Nile test-USDT, or any TRC-20 you control). Note its **contract `T…` address**.
4. **A Nile RPC endpoint:** `https://nile.trongrid.io` (a TronGrid Nile API key raises rate limits; optional).
5. **A destination `T…` address** to receive the transfer.

## Option A — run the gated live test (recommended, no DB needed)

The test `WithdrawalNileLiveTests` exercises build → sign → broadcast → confirm directly against Nile and
asserts the transaction is mined and succeeds. It is **skipped** unless the `CPE_NILE_*` variables are set.

```powershell
$env:CPE_NILE_RPC      = "https://nile.trongrid.io"
$env:CPE_NILE_APIKEY   = "<optional TronGrid Nile key>"
$env:CPE_NILE_PRIVKEY  = "<throwaway 64-hex private key>"
$env:CPE_NILE_FROM     = "<throwaway T-address holding the funds>"
$env:CPE_NILE_CONTRACT = "<Nile TRC-20 contract address>"
$env:CPE_NILE_TO       = "<destination T-address>"
$env:CPE_NILE_AMOUNT   = "1000000"   # base units — 1 USDT at 6 dp

dotnet test src/Gateway.Core/PaymentProcessing/Withdrawal/Tests --filter FullyQualifiedName~WithdrawalNileLiveTests
```

On success the test passes; on failure the assertion message includes the Nile explorer link
(`https://nile.tronscan.org/#/transaction/<txid>`) so you can inspect the on-chain result.

## Option B — run the full flow through the host (DB rows + ledger)

This drives the complete withdrawal state machine and ledger against Nile, so you can see every row in the
database (DBeaver). It needs the local SQL/Redis stack up (see `docs/dev-mainnet-deposit.md`).

1. In the git-ignored `appsettings.Local.json`, set:

   ```jsonc
   {
     "Withdrawal": {
       "LiveTron": true,
       "HotWallets": { "Tron": { "Address": "<throwaway T-address>", "KeyReference": "kms://tron/hot/0" } }
     },
     "Chains": { "Tron": { "RpcBaseUrl": "https://nile.trongrid.io", "ApiKey": "<optional Nile key>" } },
     "KeyManagement": { "DevSecrets": { "kms://tron/hot/0": "<throwaway 64-hex private key>" } }
   }
   ```

   `LiveTron=true` swaps the in-memory signer/engine for the **real** TRON builder + signer + broadcaster, and
   turns on the real chain source (needed for the confirmation tip/finality). Keep it `false` otherwise.

2. Register the Nile TRC-20 token as the canonical USDT asset (its `AssetId` must match what the edge uses):
   in `appsettings.Development.json` under `Blockchain:Assets`, point the USDT entry's `ContractAddress` at your
   Nile token contract.

3. Run the host, then submit a signed `POST /api/v1/withdraw` (use `tools/dev/Invoke-MerchantRequest.ps1`, or
   Swagger's auto-signing). The workers build → sign → broadcast → confirm; the ledger settles.

4. Inspect the result in DBeaver:
   - `withdrawal.Withdrawal` — the row moves `Approved → Signing → Broadcast → Confirmed`; `SignedTransaction`
     (the broadcast blob) and `TransactionHash` (the tx id) are populated.
   - `ledger.*` — reserve then settle journals; `MerchantLiability` down by amount+fee, `TreasuryAsset` down by
     the amount that left the chain, `FeeRevenue` up by the fee, `WithdrawalClearing` back to zero.

## Notes

- **Double-send safety.** A transaction's id is fixed once signed; the signed blob is persisted before
  broadcast, and any re-processing re-broadcasts *that* blob (same id, which the chain dedups) rather than
  building a new one. This matters on TRON specifically, where the node stamps a fresh reference/expiry at build
  time — see the note on `TronTransactionBuilder`.
- **Scope.** TRC-20 transfers only (the product's USDT-on-TRON path). Native-TRX withdrawals use a different
  transaction type and are a documented follow-up.
- **Fee limit.** `Chains:Tron:FeeLimitSun` (default 100 TRX) caps the energy the node may burn building the
  call; a TRC-20 transfer needs far less. Raise it only if a transfer is rejected for fee limit.
