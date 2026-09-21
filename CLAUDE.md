# CLAUDE.md — Crypto Payment Gateway (CryptoPaymentEngine)

> Solution/namespace root: **CryptoPaymentEngine**. Full architecture reference:
> `Scaffolding.md` (Modular Monolith + DDD guide). This file is the terse operating
> manual; `Scaffolding.md` is the detailed structural spec. If they ever disagree,
> this file wins — update it, don't drift. `claudeConvo.md` holds the historical
> decision log from before the Modular Monolith pivot.

## 1. What This Is
A crypto payment gateway: users get deposit addresses, funds are detected
on-chain and credited to an immutable ledger, and withdrawals are built, signed,
and broadcast back on-chain. Chains at launch: **TRON, Ethereum, Solana**. Adding
a chain must not change existing business logic.

Money correctness and auditability outrank everything. The ledger is the product.

Built as a **Modular Monolith with DDD** (see §4): every business capability is an
independently owned module today, extractable into its own microservice later with
minimal change. Modules never call each other's internals — only events/contracts.

## 2. Stack
- **Runtime:** .NET 10 (LTS). Nullable + implicit usings + file-scoped namespaces on.
- **Architecture:** Modular Monolith + DDD (§4). Dependencies point inward per module;
  Domain depends on nothing; modules depend on nothing of each other except
  Contracts/Events.
- **RDBMS:** **SQL Server** via **EF Core 10** (`Microsoft.EntityFrameworkCore.SqlServer`,
  first-party) — system of record for accounts, ledger, withdrawals, idempotency.
- **Cache / locks:** **Redis** (StackExchange.Redis) — distributed cache + distributed locks.
- **Documents:** **MongoDB** — raw chain payloads, webhook logs, audit trail. Never a source of truth for money.
- **Cross-module events:** in-process `IEventBus` + Outbox pattern now, Kafka-ready by
  contract (§7.5). No message broker is wired yet — do not add one without approval.
- **Background:** `BackgroundService` workers, owned per-module, registered by the host.
- **Logging:** Serilog, structured.

**Data-ownership rule:** balances/ledger live in SQL Server (ACID) and are *derived
from ledger rows*, never stored as a mutable number you overwrite. Mongo/Redis
are transient or derived. Never reconstruct a balance from Mongo. Blockchain state
is external state only — it proves a transaction occurred, it never IS a balance.

## 3. Build / Run / Verify
- Solution: `CryptoPaymentEngine.sln`
- Build:   `dotnet build`
- Run API: `dotnet run --project src/Api/MerchantGateway`
- Tests:   `dotnet test`
- Migrations: `dotnet ef migrations add <Name> -p src/Infrastructure -s src/Api/MerchantGateway`
- Config keys (required): `Db:ConnectionString`, `Redis:ConnectionString`,
  `Mongo:ConnectionString`, `Chains:<Chain>:RpcUrl`, `Chains:<Chain>:Confirmations`.
- After any runtime change: build, exercise the actual flow, report pass/fail with real output.
  Never claim "done" on code you didn't run.

## 4. Architecture — Modular Monolith + DDD
Full guide: `Scaffolding.md`. Essentials below.

### 4.1 Design order (never design from the controller downward)
    Business Requirement → Domain Model → Ledger Impact → Persistence
      → Background Processing → API → Frontend

### 4.2 Repository structure
    CryptoPaymentEngine.sln
    src/
      Gateway.Core/
        Merchant/          # partner identity, credentials, per-asset policy, webhooks
        KeyManagement/     # HDWallet, signing policy/capability/audit (custody)
        AssetManagement/   Wallet · Treasury · Sweep · Energy
        PaymentProcessing/ Deposit · Withdrawal · Settlement · Reconciliation
        Financial/         Ledger · Accounting · Billing
        Blockchain/        Providers · Scanner · Synchronization · Resources
        Platform/          Notification · Reporting · Scheduling · Workflow
      Api/
        MerchantGateway/   # merchant-facing HMAC API host (machine-to-machine) — composition root
        OperationsApi/     # internal/ops host — composition root
        MerchantPortalApi/ # merchant-facing browser portal host (session auth) — composition root
      Infrastructure/       # EF Core, Redis, Mongo, event bus/outbox impl, tech only
      SharedKernel/         # Entity, ValueObject, DomainEvent, Result<T>, IEventBus, exceptions
    tests/

`Merchant` and `KeyManagement` are top-level capabilities (like `Blockchain`); they don't fit
under the original category folders and shouldn't be forced into one.

**Built so far:** `SharedKernel`, `Infrastructure` (money type mapping, outbox, `ModuleDbContext`,
shared Redis + `IDistributedLockFactory`), `Gateway.Core/Blockchain` (Asset catalog + address
encoders + read-only capability ports `IDepositScanner`/`IChainStatusReader` in Contracts, with an
`InMemoryChainSource` adapter — the DI seam a JSON-RPC adapter later replaces), `Gateway.Core/Merchant`
(full), `Gateway.Core/KeyManagement` (HdWallet, DerivedKey, atomic index allocation, secp256k1
derivation; signing specified but NOT built), `Gateway.Core/AssetManagement/Wallet` (registry +
dedicated deposit provisioning + assignment), `Gateway.Core/Financial/Ledger` (Account ·
Journal/JournalEntry double-entry · AccountBalance cache; idempotent posting, per-account distributed
lock + rowversion, deposit credit/reversal **and withdrawal reserve/settle/release** — reserve's
negative-balance guard IS the atomic sufficiency check; consumes Deposit + Withdrawal events; DB
append-only DENY staged), `Gateway.Core/PaymentProcessing/Deposit` (detection→confirmation→reorg state
machine, dedup on `(Chain,TxHash,OutputIndex)`, resumable scan cursor, per-chain policy from config,
scanner + confirmation workers, publishes `DepositConfirmed`/`DepositOrphaned` via outbox → Ledger
credits), `Gateway.Core/PaymentProcessing/Withdrawal` (money-out state machine
Reserving→Approved/PendingApproval→Signing→Broadcast→Confirmed / Rejected / Failed; synchronous ledger
reserve via `IWithdrawalLedger` Contract, idempotent on client key, approval above threshold §10,
build→sign→broadcast→confirm→settle behind ports with in-memory fakes, publishes
`WithdrawalConfirmed`/`WithdrawalFailed` via outbox → Ledger settle/release), `Api/MerchantGateway`.
**Signing boundary (§10):** `ISigner` (KeyManagement.Contracts) + `ITransactionBuilder`/
`ITransactionBroadcaster` (Blockchain.Contracts) — the app only ever holds unsigned/signed blobs, never
keys. Dev/test use `InMemorySigner` (never touches a key) + `InMemoryTransactionEngine`; **real per-chain
tx-build/sign/broadcast + KMS-backed signer are NOT built** (deferred like the JSON-RPC adapters).
**Chain adapters (real JSON-RPC):** **TRON built** — `Blockchain/Infrastructure/Providers/Tron`
(`TronChainAdapter` over `ITronRpc`/`TronRpc`: TRC-20 deposits via eth-compatible `eth_getLogs`,
solidified block as finalized height; hand-rolled resilient JSON-RPC over a typed `HttpClient` +
`AddStandardResilienceHandler`). Money-critical EVM-hex↔TRON-Base58 conversion + log mapping are
fixture-unit-tested (USDT-TRC20 vector); **live-node round-trip deferred to staging** (needs an
endpoint/API key), and **native TRX** (non-contract) detection is a documented follow-up. Per-chain
resolution via `RoutingChainSource` (the §8 `For(chain)` pattern); swap dev↔prod by DI
(`AddInMemoryChainSource` ↔ `AddJsonRpcChainSources` + `AddTronChainAdapter`). **Ethereum/Solana
adapters NOT built yet.**

**Host composition + outbox dispatcher (tasks b & c) — DONE.** `Api/MerchantGateway` composes every
module (Merchant · KeyManagement+encoding · Wallet · Ledger · Deposit · Withdrawal), the deposit
scanner/confirmation + withdrawal processing/confirmation workers, and `OutboxDispatcher<DepositDbContext>`
+ `OutboxDispatcher<WithdrawalDbContext>` — the durable relays that publish each module's outbox to
`IEventBus` → the Ledger handlers (Deposit credit, Withdrawal settle/release), making the money-in AND
money-out paths live end to end (integration-tested). Delivery is at-least-once + idempotent; a per-module
Redis lock single-flights dispatch; `InProcessEventBus` surfaces handler failures (AggregateException) so a
failed handler leaves the message for retry; Redis uses `AbortOnConnectFail=false` so the host boots even
when Redis is down. Dev registers `AddInMemoryChainSource` + `AddInMemoryTransactionEngine` +
`AddInMemorySigner` + `AddDevelopmentKeyCustody` (dev-only in-memory `ISecretProvider` + idempotent HD-wallet
seeder — public xpub only, so a signed `/deposit` provisions an address on a fresh clone; config-substitutable
via a git-ignored `appsettings.Local.json`, e.g. paste a real branch xpub to derive prod addresses locally) +
`AddDevelopmentMerchantSeed` (dev-only idempotent seeder for one **active** test merchant with fixed, documented
credentials — the auth half of the round-trip); config in `appsettings*.json` (`Db`/`Redis`/`Mongo`/`Chains:Tron`/
`Deposit:Policies`/`Withdrawal:Policies`/`Withdrawal:HotWallets`/`KeyManagement:DevWallets`+`DevSecrets`/
`Merchant:DevSeed`). A **full signed `/api/v1/deposit` → provisioned address → `/pay/{ref}/info` round-trip runs
in dev** on a fresh clone (guide: `docs/dev-round-trip.md`; signing helper: `tools/dev/Invoke-MerchantRequest.ps1`).
**Not yet wired (prod):** a real KMS-backed
`ISecretProvider` + prod HD-wallet rows (address provisioning is dev-only until these land — never an in-memory
seed in prod, §10), and the real per-chain tx-build/sign/broadcast + KMS signer (withdrawal processing is inert
in prod until these land — never a fake signer, §10).

**Money-spine integration (teammate's shipping USDT-on-TRON app → our spine) — DONE, 319 tests green.**
The partner's *frozen* merchant contracts now run on our architecture. `Api/MerchantGateway` exposes
`POST /api/v1/{deposit,withdraw,balance}` + `GET /pay/{ref}/info` behind `MerchantSignatureMiddleware`
(X-Api-Key + HMAC over `"{ts}\n{body}"`, 5-min window, verified by Merchant's `IMerchantRequestVerifier` —
the signing secret never leaves the module); money crosses base-unit↔display only at the edge
(`AmountConversion`, which refuses over-precision, never truncates). New **`PaymentProcessing/PaymentIntent`**
module (schema `paymentintent`): the deposit *invoice* + pooled/reused deposit address (concentration for
low sweep gas), reservation arbitrated by a filtered `WHERE [Status]='Waiting'` UNIQUE index (no distributed
lock), FIFO-matched to `DepositConfirmed` (which gained `WalletId`), idempotent per-deposit (closes the
address-reuse redelivery hole); raises `PaymentIntentMatched`. New **`Platform/Notification`** module:
consumes `PaymentIntentMatched` → builds the frozen callback payload → signs via `IMerchantCallbackSigner`
→ POSTs behind an `IWebhookSender` port, durable on the PaymentIntent outbox. Merchant credential gained an
**encrypted-at-rest signing secret** (`AesGcmSecretCipher` — real AES-256-GCM, KMS-swappable key); Ledger
gained `ILedgerQuery` (derives `/balance` from `MerchantLiability`); Wallet provisioning promoted to
`IDepositAddressProvisioner` (Contracts); Blockchain gained `ConfigurationAssetCatalog` (one canonical
USDT-TRON `AssetId`). End-to-end composition proven (`MoneyInCompositionTests`: one `DepositConfirmed` →
{ledger credit + invoice match} → signed callback). **Dev `ISecretProvider` + seeded HD wallet — DONE**
(`AddDevelopmentKeyCustody`, above): a signed `/deposit` now provisions a real address in dev (host-boot
verified) **and dev merchant seeding — DONE** (`AddDevelopmentMerchantSeed`): a full signed
`/api/v1/deposit` round-trip is proven over HTTP in dev (returns the published TRON vector address), with
`docs/dev-round-trip.md` + `tools/dev/Invoke-MerchantRequest.ps1` for the next engineer.
**Follow-ups:** `/transactions/query`, withdrawal callbacks, and a dedicated callback delivery worker were all
**since built** (see the back-office + callback-delivery milestones near the end of this "Built so far" section).
Development auto-migration remains a gap (the host doesn't migrate on boot — dev DB is migrated manually per
`docs/dev-round-trip.md`).

**`AssetManagement/Energy` (Phase 5a — TRON resource monitoring) — DONE, 343 tests green.** Energy is
**TRON-specific** (ETH/SOL have no energy — gas is just native-coin balance, a future Treasury top-up concern,
NOT this module). 5a is **read-and-record only**: it moves no money, holds no keys, writes no ledger entry —
by rule (Energy must never touch deposits/withdrawals/balances/ledger). `EnergyPolicy` (SQL, schema `energy`:
per-`(Chain,WalletType)` energy thresholds + 5b stake/rent triggers) drives a `ResourceMonitorWorker` that,
per platform wallet, reads on-chain resources via **`IAccountResourceReader`** (Blockchain.Contracts §8 port;
dev `InMemoryAccountResourceReader`, real TRON `getaccountresource` adapter deferred to staging like the other
JSON-RPC adapters), classifies energy Healthy/Low/Critical against the policy, and upserts a **MongoDB**
snapshot + append-only history (`WalletResource`/`ResourceHistory` — **the codebase's first Mongo use**;
derived/observability, never money truth §2), logging an alert on Low/Critical. Wallet gained
`IPlatformWalletDirectory` (Contracts — lists non-merchant platform wallets to monitor). **Deferred:** ledger/
accounting for energy cost (→5b, when staking/rental actually spends TRX: Energy raises cost events →
Accounting posts a *platform* journal, keeping "no off-ledger money" without Energy writing ledger rows),
on-chain stake/delegate/rent (→5b, behind the §10 signing boundary), rental+forecasting+cost-optimization (→5c),
deposit-address energy (→5b, with Sweep coordination), bandwidth thresholds, and an `EnergyLowAlert` integration
event → Notification (5a alert is a structured log). A live dev demo also needs a seeded platform wallet +
policy (not built). `EnergyDelegation`/`StakePosition` tables come in 5b when first written.

**`AssetManagement/Energy` Phase 5b (stake + delegate + auto-sweep coordination) — DONE, 528 tests green.** The
action phase: it acquires and routes energy so sweeps/withdrawals don't burn TRX. **Ledger impact is NONE** (user
chose *defer cost accounting to 5c*): staking/delegation lock *recoverable* TRX (freeze/delegate, not spend), so —
like Sweep — 5b posts no ledger entry; real cost accounting (rented energy / burned TRX) waits for 5c. **One
`EnergyOperation` aggregate** (schema `energy`, `Kind ∈ {Stake, Delegate}`), a single sign→broadcast→confirm state
machine mirroring Sweep (DRY §12 — consolidated instead of separate StakePosition/EnergyDelegation tables); two
filtered unique indexes enforce one in-flight Stake per staking wallet + one in-flight Delegate per (chain,target).
Migration `EnergyOperations`; `90-energy.sql` regenerated (QUOTED_IDENTIFIER header re-added). **Staking = its own
wallet+key** (`WalletType.Energy`/`DerivationPurpose.Energy`, resolved via the existing `IPlatformWalletDirectory`
+ `IPlatformSigningKeyDirectory` Treasury seam); **signing deferred like withdrawal/sweep** (in-memory dev, inert
prod until KMS, §10). New Blockchain port **`IResourceOperationBuilder`** (freeze/delegate tx build; in-memory fake,
real TRON `FreezeBalanceV2`/`DelegateResource` adapter deferred to staging). **Auto-sweep coordination:** new
Energy Contract **`IEnergyDelegationService.EnsureEnergyForTransferAsync`** → `Ready/Provisioning/Unavailable`;
`SweepProcessingService` calls it BEFORE building — not Ready ⇒ the sweep stays Pending and retries (never fails,
funds safe), Sweep→Energy.Contracts only (§4.5). Dev stays green (in-memory resource reader reports healthy energy ⇒
Ready immediately, no delegation). Workers: StakeReplenish (policy-driven auto-stake) + operation processing +
confirmation. **Still deferred:** cost accounting (5c), undelegate/unstake reclaim, real TRON resource adapter,
`EnergyLowAlert`→Notification (kept as 5a log), seeded dev staking wallet+policy, rental+forecasting (5c). Known
interaction: when TRX becomes a *reconciled* asset, `eth_getBalance` returns only *available* TRX — frozen/delegated
must be counted or Reconciliation sees false drift (TRX isn't reconciled today).

**`AssetManagement/Energy` Phase 5c (cost-accounting spine, first cut) — DONE, 531 tests green.** The
"no off-ledger money" completion that Sweep/5b deferred here — really a **cross-module ledger spine**, not just
Energy. User chose **cost-accounting spine** + **expense-vs-PlatformFunding** model. Platform gas is booked
`DEBIT NetworkFeeExpense / CREDIT PlatformFunding` — new **`AccountType.PlatformFunding`** (credit-normal, equity-like,
**NOT chain-reconciled**, so TRX never enters custody reconciliation, respecting the 5b deferral). Both System-owned,
denominated in a **gas `AssetId`** deliberately kept OUT of the deposit catalog (config `Accounting:GasAssets:{Chain}`,
so a gas denomination never turns on native-coin deposit scanning). New `JournalReferenceType.GasCost`, idempotent
per operation id; **AccountType/JournalReferenceType are strings ⇒ NO migration.** New `ILedgerPoster.RecordGasSpentAsync`
(zero fee ⇒ `PostingOutcome.NoChange`). Fee source: **`TransactionStatus.FeeSun`** (Blockchain.Contracts, default 0;
real TRON reads `fee` from `gettransactioninfobyid`, in-memory = 0 ⇒ **dev books no gas**). **Wired for withdrawals
only (v1):** `WithdrawalConfirmationService` reads the fee + resolves the gas asset (`GasAccountingOptions`), carries
them on `WithdrawalConfirmed` (new backward-compatible `GasAssetId`/`GasFeeBaseUnits` fields); the Ledger's
`WithdrawalConfirmedHandler` books gas after settle — durable via the existing outbox, idempotent, a separate journal
from settle, and the Ledger stays chain-agnostic (fee arrives as data). **Deferred:** Sweep + Energy-operation gas
capture (need their own outbox/events, then reuse `RecordGasSpentAsync`), rental (3rd-party energy marketplace — needs
an external provider port), forecasting (ResourceHistory analytics), undelegate/unstake reclaim.

**Hybrid fee model (per-merchant pricing) — DONE, 369 tests green.** Fees are per-merchant `fixed + %`
pricing, homed on `MerchantAssetPolicy` as a `FeeSchedule` value object (the fee math lives in the domain,
floored, unit-tested) and exposed via **`Merchant.Contracts/IMerchantFeeSchedule`** — the one seam Deposit,
Withdrawal, and the Ledger split read (never the aggregate). Unpriced merchant ⇒ **zero fee** (a documented
ops gap, never an overcharge). **Deposit = payer-on-top:** `PaymentIntent` grosses the invoice up
(`GrossUpForDeposit`) so the merchant nets their requested amount, and the Ledger splits the confirmed deposit
`Dr TreasuryAsset(gross) / Cr MerchantLiability(net) / Cr FeeRevenue(fee)` — the split is resolved from the
*received* amount in the Ledger handlers, so it stays independent of any invoice (an intent-less deposit is
still priced; fee=0 collapses to the original 2-line journal). **Withdrawal:** the merchant bears the
`fixed + %` fee (moved off the config `WithdrawalPolicy.Fee` onto the per-merchant schedule; config keeps
limits/approval/confirmations), and the platform bears **gas** — the TRX network-fee expense stays the 5b
Energy/Accounting cost path (USDT `FeeRevenue` sizes to cover it; different assets ⇒ different journals).
Reorg reversal re-derives the same fee from the same confirmed amount (a fee-schedule change mid-reorg is a
documented, near-zero-probability follow-up → carry the fee on the event, Withdrawal-symmetric).

**Per-merchant HD wallets (separate seed each) — DONE.** The shared deposit HD wallet is replaced by **one HD
wallet per merchant, its own seed** (custody blast-radius: one merchant's key compromise can't expose
another's, §10). `HdWallet` gained `MerchantId`; the unique index is now `(MerchantId, Chain, Purpose)` filtered
Active — SQL Server's single-NULL rule keeps one platform wallet per `(chain, purpose)` while each merchant
owns one. Wallets are **created on first deposit** (`IWalletDerivation.AllocateNextForMerchantAsync` →
`IHdWalletProvisioner`), the unique index arbitrating the create race (lost race adopts the winner); each
merchant's `NextDerivationIndex` is an independent atomic sequence. The address pool + one-payment-per-address
lock (PaymentIntent) and sweep economics are **unchanged** — address count per merchant is the same. Dev:
`DevHdWalletProvisioner` mints a **deterministic-per-merchant** seed (fixed dev salt + merchant id, via NBitcoin)
and writes only the account **xpub** (public) to a new writable `MutableInMemorySecretStore` — watch-only from
then on, no seed in the dev store (§10). **Prod deferred:** no `IHdWalletProvisioner` is registered, so
per-merchant minting is inert (never an in-memory seed in prod) until a KMS-backed provisioner lands, behind the
same port. The dev round-trip address is now per-merchant deterministic (not the old shared `TUEZSdK…` vector;
`docs/dev-round-trip.md` updated).

**Human-testable mainnet deposit harness (migrating the PoC's proven deposit flow onto our spine) — DONE, 369
tests green, host-boot verified.** Reuses the legacy PoC's `pay.html` **unmodified** (served at `/pay/{ref}` via
`UseStaticFiles`; our `/pay/{ref}/info` already returns its exact `{address,amount,expiresAt,status}` contract,
status already mapped to `pending/confirmed/expired`). Adds dev-only **Swagger** (`/swagger`, Swashbuckle 10.2.3)
that **actually exercises the signed flow**: `Security/DevSwaggerRequestSigning.cs` injects a swagger-ui
requestInterceptor computing `hex(HMAC-SHA256(hexDecode(secret),"{ts}\n{body}"))` in the browser from the
`Merchant:DevSeed` credentials, so "Try it out" works with the three `X-` headers left blank (they're documented
`Required=false` precisely so swagger-ui doesn't block Execute; the middleware still enforces them on the wire).
Dev-only + only when `Merchant:DevSeed:Enabled` — a real signing secret must never be embedded in a page (§10);
`tools/dev/Invoke-MerchantRequest.ps1` remains the PowerShell equivalent. (The PoC's Swagger could **not** do
this — it declared only an `X-Api-Key` scheme while `MerchantSecurityFilter` still demanded the signature, so its
deposit flow was proven with a signing client, never through Swagger.)
`docker-compose.yml` brings up **SQL Server :1433 (DBeaver-reviewable) + Redis :6379 (callbacks need it) + Mongo
:27017**; `tools/dev/Setup-LocalEnv.ps1` creates the DB + applies all 9 module migrations; per-developer secrets
live in the git-ignored `appsettings.Local.json`. The dev host now takes the **real TRON adapter when
`Chains:Tron:Live=true`** (+ a fresh TronGrid key — NEVER the leaked one), else the in-memory source; signer/keys
stay in-memory (deposit detection never signs, §10). New dev-only host endpoints (`Endpoints/DevEndpoints.cs`,
mapped only in Development): **`/dev/callbacks`** (in-host sink so a human sees the signed merchant callback the
`HttpWebhookSender` fires on detection) and **`/dev/scan-cursor`** (seeds the scan cursor *behind* the tip via
`?lookback=N`, for a transfer already sent — the scanner cold-starts at the tip on its own, it does NOT crawl
from genesis). For recoverable/private mainnet addresses the
dev provisioner takes an optional real account xpub (`KeyManagement:DevMerchantXpub` at `m/44'/195'/0'/0`), else
the throwaway public-salt seed (test-only). Full runbook: `docs/dev-mainnet-deposit.md`. **Deferred/known:** the
live TRON adapter's first mainnet exercise may need rate-limit/confirmation tuning (was deferred to
staging); withdrawal stays inert in dev (no real signer). We did NOT port the legacy MVC controllers — they drag
in the old `Core`/`UsdtService` projects; the flow is reproduced in our minimal-API edge (§15).

**Deposit-flow hardening + fresh-env repair — DONE, 393 tests green, proven end-to-end over HTTP.** Four real
defects found by reviewing the deposit use case:
1. **`db/sql` had drifted from the migrations** (`AddMerchantAllowedIps`, `AddDepositsReceivedCount`,
   `AddPaymentIntentGracePeriod` shipped in `0f05da8` without regenerated scripts). A fresh environment built
   from `db/sql` therefore booted onto a schema the code couldn't use: the dev merchant seed died on
   `Invalid column name 'AllowedIpsCsv'`, so **every signed `/api/v1` call returned 401 "Invalid API
   credentials"** and the deposit use case was untestable. Scripts regenerated; `db/README.md` now carries a
   drift-check + the `SET QUOTED_IDENTIFIER ON` header rule (both are silent-failure traps).
2. **Unbounded reorg tracking:** `GetTrackableAsync` never retired confirmed deposits, so the tracker re-read one
   block per deposit *ever taken*, per pass, forever — unbounded RPC growth that would exhaust TronGrid's limit.
   Fixed with a nullable `FinalizedAt` **tracking marker** (NOT a new status — `Status` stays the money
   lifecycle) set once the block passes the chain's irreversibility point; `IX_Deposit_Chain_Status` is now
   filtered `WHERE [FinalizedAt] IS NULL`. **Only a Confirmed deposit settles** — retiring a still-Detected one
   on finality alone would strand it uncredited on any chain whose finality precedes the policy depth (TRON
   solidifies ~19, a 20-conf policy hits exactly this). Migration `20260717072121_DepositFinalizedAt`.
3. **The pay page shipped with no CSP:** `PayEndpoints` had a dead `GetPageAsync` holding the security headers
   while the live route was an inline lambda that skipped them — so the CSP landed on the JSON `/info` (useless)
   and not on the HTML that renders the address and loads a CDN QR script. Wired up; verified by response headers.
4. **`Gateway:BaseUrl` pointed at `:7114`** while the host listens on `:51078/:51079`, so the `payUrl` handed to
   the payer was a dead link. Now pinned to `launchSettings.json` and commented as such.

**Proven:** from a database built *only* from `db/sql`, a signed `POST /api/v1/deposit` returns
`{referenceNo, address: T…, payUrl}`, `/pay/{ref}/info` returns the pay-page contract, and `/pay/{ref}` serves
the page with its CSP. `Api.IntegrationTests` contains **zero tests** (builds, discovers none) — false comfort,
worth filling.

**`AssetManagement/Treasury` (single hot withdrawal wallet) — DONE, 495 tests green.** Withdrawal used to source
its signing wallet from raw config (`Withdrawal:HotWallets`, read by a now-deleted `ConfigurationHotWalletProvider`);
Treasury replaces that with a real, DB-backed, auditable registration. **Persistence-less module** (Application +
Contracts + Infrastructure + Tests — no Domain/DbContext/schema; precedent: `Platform/Notification`): every fact
lives in `Wallet.Wallet` + `KeyManagement.HdWallet`, composed via their Contracts (§4.5). The wallet it registers
is `WalletType.HotWithdrawal` / `HdWalletPurpose.Withdrawal`; `WalletType.Treasury` stays reserved for a future
cold reserve. **The real landmine (fixed):** the hot wallet's key was **imported flat** (a standalone throwaway
private key, not BIP-32 derived), so it has no real xpub — `HdWallet.Create` requires a non-null
`PublicKeyReference` for secp256k1, and a fabricated one would let `WalletDerivationService` silently derive bogus
child addresses. Fix is structural: **`HdWallet.IsImported`** flag + **`CreateImportedPlatformKey`** factory
(`PublicKeyReference=null`) + a **guard** in `AllocateFromAsync` returning `ImportedKeyCannotDerive` (migration
`AddHdWalletIsImported`, constraint relaxed to allow `IsImported=1 AND PublicKeyReference IS NULL`). New Contracts:
KeyManagement `IPlatformSigningKeyDirectory` (read a signing `KeyReference` by `(Chain,Purpose)`) +
`IPlatformKeyRegistrar` (write, idempotent-adopt; registered only in `AddDevelopmentKeyCustody`, so a real
production registrar stays absent-not-just-unused §10); Wallet `IPlatformWalletRegistrar`; Treasury
`ITreasuryHotWalletDirectory` (combines both, fails loudly if 0 or >1 hot wallet for a chain). Withdrawal's
`IHotWalletProvider` went sync→**`ForAsync`** and is now backed by `TreasuryHotWalletProvider`. Dev seeds via
`AddDevelopmentTreasuryHotWalletSeed` (`Treasury:DevHotWallets` + a colon-free `KeyManagement:DevSecrets` ref —
a colon in a DevSecrets key is silently truncated by config's `GetChildren()`). **Prod deferred:** no
`IPlatformKeyRegistrar` registered ⇒ hot-wallet registration inert until a KMS-backed one lands; the real
KMS-backed `ISigner` is still deferred (withdrawal stays inert in prod until it + the registrar arrive, §10).
KMS custody plan agreed (customer-managed keys; two `ISigner` impls — KMS-asymmetric single-address for
hot/treasury, envelope-encrypted HD seed for merchant deposit sweeps; staking gets its own wallet+key) — NOT built.

**`PaymentProcessing/Reconciliation` (first cut — ledger-vs-on-chain custody audit) — DONE, 495 tests green.**
Second in the confirmed Treasury → Reconciliation → Sweep order. **Persistence-less SQL, no migration** — every
fact is a read; it persists only Mongo observability snapshots (§2), read-and-record like Energy 5a. **The
invariant:** Ledger `TreasuryAsset(asset)` balance == Σ on-chain balance across every controlled address (platform
wallets + every funded deposit address). `TreasuryAsset` is the only ledger-derivable custody figure — the ledger
tracks accounting buckets, never addresses (§8). Drift = onChain − ledger. **Log-only**, moves no money, holds no
keys. Classifies **Incomplete** (an address couldn't be read ⇒ partial total ⇒ never a false Balanced/Drift),
**Balanced** (`|drift| ≤ Reconciliation:DriftTolerance`, config default 0 base units — absorbs known transients
like unconfirmed deposits / in-flight withdrawals, but the exact drift is always recorded), else **Drift** (log
Warning). Snapshot + history in Mongo collections `Reconciliation`/`ReconciliationHistory`; worker per chain,
5-min interval, always-on. **Three new read-only ports (additive, migration-free):** `ILedgerQuery.GetTreasuryHoldingAsync`;
Blockchain `IBalanceReader` (the §8-planned port — `InMemoryBalanceReader` for dev + real `TronBalanceReader`:
TRC-20 via `eth_call balanceOf`/`TronAbi.EncodeBalanceOf`, native TRX via `eth_getBalance`; live round-trip
deferred to staging); `IWalletDirectory.ListReceivingDepositAddressesAsync` (funded Deposit wallets, all merchants,
`DepositsReceivedCount>0`). In dev the in-memory `IBalanceReader` reads 0, so drift == the ledger holding until the
real TRON adapter is live. **Ops read API — since built** (`GET /api/v1/ops/reconciliation` in OperationsApi; see the
reconciliation-ops milestone below). **Deferred:** per-asset tolerance, sustained-vs-transient drift detection, and a
history/time-series read (the snapshot store's `ListAsync` returns only the current per-(chain,asset) snapshots);
reconciliation simplifies greatly post-Sweep (deposit addresses drain to ~0, custody concentrates in the hot wallet).

**`AssetManagement/Sweep` (concentrate deposit balances → hot wallet) — DONE, 511 tests green.** Third in the
Treasury → Reconciliation → Sweep order, and the ONE module that signs and physically moves funds (built strict-T3
with design approval first). Schema `sweep`, full 8-layer shape. **Ledger impact is NONE** (the key correctness
point): a sweep relocates funds between addresses the platform already controls, so total custody
(`TreasuryAsset`) is unchanged ⇒ no ledger entry for the principal — exactly why Reconciliation sums deposit + hot
addresses (invariant across a sweep). The only cost is gas, a platform expense on the deferred Energy 5b Accounting
path. **State machine mirrors Withdrawal minus the reserve:** `Pending → Signing → Broadcast → Confirmed / Failed`;
the signed blob is persisted before broadcast (crash-retry re-broadcasts the SAME tx, chain dedups); Fail only
pre-broadcast; a filtered unique index `UX_Sweep_InFlight_Wallet_Asset` (WalletId, AssetId)
`WHERE [Status] IN ('Pending','Signing','Broadcast')` enforces one in-flight sweep per (wallet, asset), `TryAddAsync`
the create-race arbiter. **Scope (chosen):** threshold-triggered (`Sweep:Policies:{Chain}:MinSweepAmountBaseUnits`),
gas deferred to Energy 5b. **Signing** is the KMS Tier-2 envelope path: signs FROM the deposit address via a new
KeyManagement Contract **`IDepositSigningKeyDirectory`** (address → `DepositSigningKey.KeyReference`, the internal
composite `"{secretReference}#{index}"` the future envelope signer parses), reusing the SAME
`ITransactionBuilder`/`ISigner`/`ITransactionBroadcaster` ports as Withdrawal — **real signer deferred like
withdrawal** (in-memory in dev/testnet, inert in prod until KMS lands, §10). A real TRON mainnet sweep also needs
energy delegated to the deposit address (5b) or a TRX top-up. Three always-on workers (scan 2 min → process 15 s →
confirm 15 s) consume Contracts only (§4.5): Wallet `ListReceivingDepositAddressesAsync`, Blockchain
`IBalanceReader`/builder/broadcaster/`IChainStatusReader`, KeyManagement `IDepositSigningKeyDirectory`/`ISigner`,
Treasury `ITreasuryHotWalletDirectory`. `db/sql/120-sweep.sql` regenerated (QUOTED_IDENTIFIER header re-added for
the filtered index).

**Withdrawal hot-wallet float gate + funding holds (three-tier hot/cold custody, Phase 1) — DONE, 540 tests green.**
The target custody topology: deposit addresses (hot) → **treasury (cold, key NOT in system — a human signs a reload
in the UI)** → **withdrawal hot wallet (a float that drains)** → merchant payout. **The correctness key: ledger
sufficiency ≠ physical sufficiency.** The merchant's ledger reserve says they're *owed* the money; it says nothing
about whether the hot wallet physically holds enough to broadcast. So before signing, `WithdrawalProcessingService`
now reads the hot wallet's on-chain balance (`IBalanceReader`) minus in-flight committed outflows
(`SumInFlightOutflowsAsync` over `Signing`/`Broadcast` — a batch can't each see the same funds and overspend), and:
`available < amount` ⇒ **park `AwaitingFunds`** (reserve **HELD**, NOT released — a hold is a deferral, not a
`Failed`), reason recorded for ops, **auto-resumes** next pass once the float is reloaded; `available ≥ amount` but
`amount > ApprovalThreshold` (the user-chosen "preset") ⇒ **`AwaitingRelease`**, a large payout waits for an explicit
operator release (the "large = manual" rule; `ReleasedAt` gates it so a later fund dip never demands a second
release); else it sends. Two new statuses (`AwaitingFunds`/`AwaitingRelease`), `Withdrawal` transitions
`Park`/`MarkAwaitingRelease`/`ResumeToApproved`/`ReleaseForSend`/`Cancel` (cancel is the one hold→`Failed` path that
releases the reserve), new columns `StatusReason`/`ReleasedBy`/`ReleasedAt` + Status widened 16→24 (migration
`AddWithdrawalFundingHold`, `70-withdrawal.sql` regenerated). Ops seam: `IWithdrawalFundingService` +
`OpsWithdrawalFundingEndpoints` (`/release`, `/cancel`); `IWithdrawalDirectory` admin rows expose `StatusReason` +
effective statuses `insufficient_balance`/`awaiting_release`. Notification is **status + structured log only** (push
alert deferred, Energy-5a style). Dev: `DevHotWalletFloatSeeder` seeds the in-memory reader so the dev happy-path
still sends (`Withdrawal:DevHotWalletFloatBaseUnits`, default 1,000,000 USDT; set low to demo the park path; no-op
under live TRON, which reads the real balance). **Deferred (agreed):** Phase 2 = the treasury **cold reload** flow —
build-unsigned treasury→hot transfer, **human signs client-side in the browser (key NEVER hits the backend, §10)**,
backend broadcasts; plus redirecting **Sweep's destination to the cold treasury** (today it targets the hot wallet).
Also agreed but not yet built: **reverting deposit wallets from per-merchant HD back to one shared platform pool**
(the user's Q1 choice — its own phase, supersedes [[per-merchant-hd-wallets]]).

**Withdrawal hot-wallet POOL (Option B: HD, one seed → N children) — DONE, 546 tests green.** The single hot wallet
became a **pool**: each wallet processes **one transaction at a time, leased from sign until it CONFIRMS** (not freed
at broadcast). Custody model chosen **Option B** (after weighing the two-factor/blast-radius trade-off): the pool is
one **platform withdrawal HD wallet** (its own seed + xpub) whose N watch-only child addresses are the hot wallets —
exactly the deposit model, so **no KeyManagement/Wallet/Treasury migration** (one HD wallet + N children fits the
existing schema; the imported flat-key hot wallet is gone). One envelope-encrypted seed backs the whole pool (KMS KEK
+ DB ciphertext = the two factors; either alone is useless — the property the user wanted). **KeyManagement:**
`IHdWalletProvisioner.ProvisionPlatformWithdrawalWalletAsync` (dev: deterministic seed, store xpub; optional
`KeyManagement:DevWithdrawalXpub` for real mainnet), `AllocateNextAsync(chain,Withdrawal)` now provisions-on-first-use
then derives children, `IPlatformSigningKeyDirectory.FindByAddressAsync` resolves each child's key by address
(`"{secret}#{index}"`, like the deposit signer). **Treasury:** `GetHotWalletPoolAsync` (per-address key resolution;
`TreasuryHotWallet` gained `WalletId`), `GetHotWalletAsync` returns the first (Sweep's single destination, until Phase
2); a grow-only dev pool seeder derives+registers up to `Treasury:HotWalletPool:Size` children. **Withdrawal:** new
`SourceWalletId` (stamped at sign, leases the wallet) + filtered unique index `UX_Withdrawal_InFlight_SourceWallet`
`WHERE [Status] IN ('Signing','Broadcast')` (one in-flight per wallet, the DB backstop — migration
`AddWithdrawalSourceWallet`); **`IHotWalletAllocator`** (replaces `IHotWalletProvider`): pool − leased → funded
(`IBalanceReader`) → **least-recently-used**, null ⇒ park `AwaitingFunds` (reuses the Phase-1 hold). The per-wallet
lease **replaces** the Phase-1 in-flight-sum float check — a free wallet has no unconfirmed outflow, so its balance is
exact. Confirmation frees the wallet (busy is derived from status). Dev seeds a 3-wallet pool + each child's float.
**Concurrency (multi-instance-safe, done):** correctness is the DB — `UX_Withdrawal_InFlight_SourceWallet` (no
double-lease) + `rowversion` (no double-process) — so no double-send is possible regardless of workers (§7.4: the
lock is performance, the DB is correctness). On top: a **single-flight distributed lock** in both withdrawal workers
(`withdrawal:processing`/`:confirmation`, `WorkerLoop.SingleFlightAsync`, skip-if-contended — the OutboxDispatcher
pattern) so instances don't do redundant work, and `IWithdrawalRepository.TrySaveSignedAsync` catches the
lease/rowversion conflict and reverts cleanly (detach → re-allocate next pass) if the lock is ever bypassed (Redis
down). Selection is platform-level LRU; the pool is **shared across all merchants** (not per-merchant);
**per-merchant pools + health-based rotation stay deferred** ([[wallet-rotation-health-design]]).

**Treasury cold reload + Sweep→cold (Phase 2 — the three-tier custody completed) — DONE, 561 tests green.** The
custody topology is now complete: deposit addresses (hot) → **sweep → COLD treasury** (accumulates; key NOT in the
system) → admin **reload → hot pool** (a human signs client-side) → withdrawals drain the pool. **Treasury gained
its first persistence** (schema `treasury`, Domain+Workers projects added): `TreasuryColdWallet` (watch-only address,
one per chain — homed in Treasury, NOT a keyless Wallet row, since we hold no key for it) + a `TreasuryReload`
aggregate (`AwaitingSignature → Signed → Broadcast → Confirmed / Failed`, mirrors Withdrawal/Sweep minus the reserve;
migration `InitialTreasury`, `db/sql/130-treasury.sql`). **Reload flow (human-in-the-loop, §10):** `TreasuryReloadService.InitiateAsync`
builds the **unsigned** treasury→hot transfer (`ITransactionBuilder`) to an **operator-chosen pool wallet** (returns
the unsigned tx hex); the operator **signs client-side — the cold key never reaches the backend** — and posts the
signed blob to `SubmitSignedAsync`; the single-flighted `TreasuryReloadWorker` broadcasts + confirms it. **NO ledger
entry** (treasury→hot is custody-internal — total custody unchanged, only its location, §14). On confirm the target
hot wallet's float rises on-chain → the withdrawal allocator sees it next pass → parked `AwaitingFunds` withdrawals
**auto-resume** (Phase 1 mechanism, unchanged). **Sweep redirected:** `SweepScanService` now sweeps deposits to the
**cold treasury** (`ITreasuryColdWalletDirectory`) instead of the hot pool. **Reconciliation** includes the cold
address in the custody sum (else it would report a huge false drift once sweeping concentrates funds cold). **Cold
registration:** dev config seed (`Treasury:ColdWallets`, watch-only address — no key) + `ITreasuryColdWalletRegistrar`
(the seam a prod staff ops action reuses). **Real TRON tx-build/broadcast + real browser signing deferred** like
everything else (in-memory engine in dev; inert in prod until the adapters land). **Deferred/known:** a per-chain
reload confirmation depth (currently one `TreasuryReloadOptions.Confirmations`, dev=1); and the client-side signing UI.
(The production **staff-auth Ops endpoints in OperationsApi** were since built — see the Treasury-reload-Ops milestone
below; the dev `/dev/treasury/*` helpers in MerchantGateway remain for local testing.)

**Production KMS-envelope seed custody (§10) — DONE, 567 tests green (0 failed, 5 known skips).** The seam that flips
withdrawal + sweep + address-provisioning from "inert in prod" to live money-out. **Key simplification:** because
Option B made the withdrawal pool an HD wallet and the cold treasury is human-signed, the ENTIRE launch money-out
custody surface is **one Tier-2 envelope pattern** ([[kms-key-topology]]) — the Tier-1 KMS-asymmetric signer is NOT
needed (no flat single-address signing wallet remains). **The two-factor property (user's requirement):** the seed
ciphertext lives in **this system's DB** (`keymgmt.SecretMaterial`, migration `AddSecretMaterial`) and the
key-encryption-key lives in **KMS** — either alone is useless; only compromise of both is dangerous. New
`SecretProviderKind.AwsKmsEnvelope`. **`KmsHdWalletProvisioner`** mints a CSPRNG 64-byte seed → `kms:Encrypt` under the
**per-purpose CMK** (`KeyManagement:Kms:KeyArns:{Deposit,Withdrawal}`) → stores **one** `SecretMaterial` row holding
ciphertext **and** the public xpub together, so the seed↔xpub pairing is atomic (a torn write would derive addresses no
key can sign); seed minted **exactly-once** under the create-on-first-use race via a unique `Reference` index +
adopt-on-conflict. **`KmsEnvelopeSecretProvider`** (singleton; reads its store through a scope) serves a plain ref → the
public xpub (watch-only derivation never touches the seed, §8), and a signing ref `"{ref}#{index}"` → `kms:Decrypt` the
seed **in memory only** → derive the secp256k1 child at `m/44'/coin'/0'/0/{index}` → return it as a zeroized
`SecretLease` → wipe the seed. **The signer is unchanged** — the existing `TronSigner` already does the secp256k1
recoverable signing; KMS plugs in purely at the `ISecretProvider` seam (DRYer than a separate `EnvelopeHdSigner`).
**Defence in depth:** each ciphertext is bound to a KMS **encryption context** `{reference,purpose,chain}` (AAD — one
wallet's blob can't decrypt as another's even with full KMS access), and the provider refuses to `Decrypt` under a CMK
not in config (`IsConfiguredKey`). `AWSSDK.KeyManagementService` (approved §11) referenced only by
KeyManagement.Infrastructure. **`AddAwsKmsKeyCustody`** is registered ONLY in the host's **Production** branch and only
when `KeyManagement:Kms:Enabled` — which also turns on the real TRON tx-engine + `AddTronSigner`; without it Production
registers no signer at all (never a fake signer, §10). Config is **identifiers only** (region + ARNs); the app
authenticates by its instance/task IAM role, no keys in config. Tests: `KmsEnvelopeCustodyTests` (6) incl. the
money-critical proof that the **KMS-derived signing key == the watch-only address's public key**, context-mismatch +
unconfigured-CMK rejection, exactly-once minting, and the AwsKmsEnvelope↔InMemoryDevelopment interlock (both
directions). **Deferred:** a live-AWS round-trip (staging — needs the real CMKs + IAM role; unit tests fake
`IAmazonKeyManagementService`); ETH/SOL adapters; native-TRX; and the prod OperationsApi reload/cold endpoints.

**Real TRON energy/bandwidth adapters + sweep bandwidth gate + dev staking-wallet seed — DONE, 580 tests green.** Takes the already-built
Energy 5b flow live on a real node (unblocked by the KMS signer). New segregated **`ITronResourceRpc`** (on `TronRpc`
alongside `ITronRpc`/`ITronTxRpc`, so no existing fake breaks): `getaccountresource` + `getaccount` reads and
`freezebalancev2` + `delegateresource` unsigned builds (keyless, §10). **`TronAccountResourceReader`** maps
getaccountresource (energy + free-and-staked bandwidth) + getaccount (`balance` = spendable TRX sun, `frozenV2`) →
`AccountResourceSnapshot` (TRON omits zero fields ⇒ DTOs default 0; a new account `{}` ⇒ all-zero).
**`TronResourceOperationBuilder`** builds FreezeBalanceV2(ENERGY)/DelegateResource(ENERGY) into the same
`UnsignedTransaction` the existing signer/broadcaster handle (freeze/delegate return the tx at top level or
`{"Error":<hex-ascii>}` ⇒ builder throws). Wired: reader bundled into `AddTronChainAdapter` (real whenever the real
chain adapter is used — dev-live + prod), builder into `AddTronTransactionEngine`; in-memory in the non-live dev
branch. **The sweep gate now covers energy AND bandwidth** (a TRC-20 sweep needs both): energy is delegated when
short (avoids the ~27 TRX burn); **bandwidth is NOT delegated** (its ~0.27 TRX burn is trivial) — the gate only
requires the address can pay it from free bandwidth OR a small spendable-TRX cushion (the "leftover TRX funds the
next sweep" buffer). Energy-OK-but-no-bandwidth-and-no-TRX ⇒ `Unavailable` (park, needs a TRX top-up). New
`EnergyOperationOptions.RequiredBandwidthPerTransfer` + `MinTrxCushionSun`; `InMemoryAccountResourceReader` reports
healthy bandwidth+TRX by default so dev sweeps stay Ready. Tests: `TronResourceAdapterTests` + 2 gate tests.
**Dev staking-wallet + policy seed — DONE** (`AddDevelopmentEnergyStakingSeed`/`EnergyStakingWalletSeeder`):
idempotently registers the platform staking wallet — the delegation source the gate draws from — as an imported
`Purpose.Energy` signing key + a `WalletType.Energy` row + an auto-stake `EnergyPolicy`, from `Energy:DevStakingWallets`
(empty by default; a dev fills it + a `KeyManagement:DevSecrets` key + faucet TRX in `appsettings.Local.json` for a live
Nile delegation). No schema change; `EnergyStakingWalletSeederTests` (4). **Deferred:** the PROD KMS **Energy CMK**
(`KeyArns:Energy`) + a prod staking-wallet provisioning trigger (dev seed only, §10); undelegate/unstake reclaim; and the
frozen/delegated-TRX reconciliation count (TRX isn't reconciled yet).

**Energy "gas hub" — native-TRX builder + bandwidth top-up (Pieces 1+2) — DONE, 585 tests green.** The staking/energy
wallet becomes the per-chain **TRX gas hub** (single source+sink for TRX-as-gas). (1) **Native-TRX builder:** new
**`INativeTransferBuilder`** port — deliberately its OWN port, NOT an `AssetId` path, so native TRX never enters the
deposit catalog (which would switch on TRX deposit scanning) — over `ITronTxRpc.CreateTransactionAsync`
(`/wallet/createtransaction`); `TronNativeTransferBuilder` + in-memory impl; flows through the existing signer/broadcaster.
(2) **Bandwidth top-up:** the sweep energy gate now *supplies* bandwidth-TRX instead of dead-ending — new
`EnergyOperationKind.TopUp` (a native-TRX transfer hub→short-address); `EnsureEnergyForTransferAsync`, on energy-OK-but-no-
bandwidth-and-no-TRX, creates a top-up and returns `Provisioning` (was `Unavailable`); `EnergyOperationProcessingService`
routes TopUp via the native builder, signed by the hub's `Purpose.Energy` key; filtered unique index `UX_EnergyOp_InFlight_TopUp`
(migration `AddEnergyTopUpIndex`, `90-energy.sql` regenerated). NO ledger (hub→deposit is custody-internal; the bandwidth burn
is deferred gas). **EF gotcha fixed:** two filtered indexes on the same columns need the *named-index overload* or the second
silently replaces the first. **Piece 3 (NOT yet built):** sweep residual TRX from **`Disabled` (dead) deposit wallets** → the
hub (user chose: always to the hub, no cold-overflow; trigger = the existing `WalletStatus.Disabled`). Design resolved (no
Sweep migration — reuse the aggregate with the gas `AssetId`, add `IBalanceReader.GetNativeBalanceAsync` +
`IWalletDirectory.ListDisabledDepositAddressesAsync` + an Energy gas-hub-address Contract, processing branches native).
**Inert until a wallet-deactivation flow sets `Disabled`** (nothing does in prod today — merchant Close doesn't cascade).

**Withdrawal on-demand energy gate — DONE, 586 tests green.** Closed a real gap: a USDT payout is a TRC-20 transfer FROM a
hot-pool wallet, so it needs energy like a sweep does, but `WithdrawalProcessingService` only gated on physical float — a real
mainnet payout would have burned ~27 TRX. It now calls `IEnergyDelegationService.EnsureEnergyForTransferAsync(chain, lease.Address)`
after resume-to-approved and before build/sign; not Ready ⇒ stays Approved and retries (transient), never signs without energy
(Withdrawal.Application now refs Energy.Contracts, §4.5). So the gas hub serves BOTH money-out paths. **Monitor/provision
asymmetry** (deposit vs withdrawal): deposit addresses are many+transient ⇒ NOT monitored, energy provisioned JIT by Sweep;
the withdrawal hot pool is few+fixed ⇒ monitored by 5a AND now provisioned on-demand (though 5a only *alerts* where an
`EnergyPolicy` exists — a `HotWithdrawal` policy to alert on the pool is a follow-up). Test: `WithdrawalFlowTests.Without_energy_…`.

**Back-office (`Api/OperationsApi`) + `Platform/Identity` staff auth — BUILT (in-tree; not in my earlier notes).**
A second composition-root host. `Platform/Identity` (schema `identity`, migration `InitialIdentity`) owns staff
identity: `StaffUser`/`StaffSession`, `IStaffPasswordHasher`, `IBearerTokenGenerator`, `AddDevelopmentStaffSeed`
(fixed dev Admin). `OperationsApi` sits behind `StaffBearerAuthMiddleware` and composes 11 modules for **read
Contracts only** — no workers/dispatchers, so the host scans/processes/broadcasts nothing (§4.7): staff
login/logout, merchant admin, transaction search (deposit/withdrawal/paymentintent/callback), withdrawal
**approval** (§10 above-threshold) and **funding release/cancel** (`Ops*Endpoints`). **Known placeholders:** the two
transaction screens hardcode `fee = "0"` and `userId = null` (real per-merchant fee + user attribution not yet
surfaced — `§ docs/backoffice-api.md`). **Treasury cold-reload, Reconciliation, and now Energy/Sweep** all have
ops surfaces (see the milestones below) — the earlier "no ops surface" gap for Energy/Sweep is closed (read-only).

**Reconciliation custody-status Ops read (`Api/OperationsApi`) — BUILT, full suite green.** The go-live surface for the
ledger-vs-on-chain custody audit, which was log-and-Mongo-only. New `OpsReconciliationEndpoints`: `GET
/api/v1/ops/reconciliation` (optional `?chain=`) returns the latest snapshot per (chain, asset) — ledger
`TreasuryAsset` holding vs summed on-chain total vs drift, plus status (Balanced/Drift/Incomplete),
addresses-scanned/unreadable, observed-at — with **non-Balanced rows sorted first** so an operator sees problems at the
top. Authenticated staff only (no Admin gate — it moves nothing). Amounts are base→display via `AmountConversion`
(asset decimals, §14) **plus** an exact `driftBaseUnits` string (a custody audit needs the precise integer). A pure
**read** over the snapshots the money host's `ReconciliationWorker` writes to Mongo (§2) — this host neither computes
nor writes them: new read-only `IReconciliationStore.ListAsync` + a `AddReconciliationReadModel` composition that
registers ONLY the Mongo read store (shared client), NOT the compute `ReconciliationService`/worker (which needs
`IBalanceReader` etc. the ops host lacks and must never run, §4.7). OperationsApi gained `Mongo:ConnectionString` config
(same instance as MerchantGateway). **Deferred:** a history/time-series read + per-asset tolerance (as above).

**Treasury cold-reload — production staff-auth Ops endpoints (`Api/OperationsApi`) — BUILT, full suite green.** The
go-live surface for the three-tier custody cold-reload, promoting the dev-only `/dev/treasury/*` helpers to real
Admin endpoints. New `OpsTreasuryEndpoints` (all `.RequireAdmin()`): `GET /api/v1/ops/treasury/hot-pool` (list pool
wallets to pick a reload target — never returns the signing `KeyReference`, §10), `POST .../cold-wallet` (register the
watch-only cold address via `ITreasuryColdWalletRegistrar`), `POST .../reload` (`ITreasuryReloadService.InitiateAsync`
→ builds the **unsigned** treasury→hot transfer, returns `{reloadId, unsignedTransactionHex}`), `POST
.../reload/{id}/submit` (stores the operator's client-signed blob). **Keyless + human-in-the-loop (§10):** the ops host
builds unsigned only — the operator signs the cold key **client-side** (never sent to any backend), and the money
host's `TreasuryReloadWorker` broadcasts + confirms (OperationsApi runs no workers). **No ledger entry** (custody-internal,
§14). Composition: OperationsApi gained `AddTreasuryModule` + a **keyless `ITransactionBuilder`** — tiered
`AddInMemoryTransactionEngine` (Development/Staging) ↔ `AddTronTransactionEngine` (Production, registered
**unconditionally**, NOT gated on KMS, since the reload is human-signed not KMS-signed; the builder never crosses a key).
Amount is display→base at the edge via `AmountConversion` (asset decimals, §14) — not the dev endpoint's hardcoded
`×1e6`. Endpoints are thin wrappers over the already-tested `TreasuryReloadService`/registrar/directory (Ops endpoints
follow the service-tested, not host-tested convention — no OperationsApi test project). **Deferred:** USDT-only first cut
(multi-asset later); dev testing needs a hot pool seeded by MerchantGateway on the shared DB; the per-chain reload depth
+ client-side signing UI stay as noted above.

**Callback delivery completion — BUILT.** `Platform/Notification` now also consumes **`WithdrawalConfirmed` /
`WithdrawalFailed`** (merchant withdrawal callbacks) alongside `PaymentIntentMatched` / `PaymentIntentFailed`,
delivered by a **dedicated `CallbackDeliveryWorker`** (attempt / backoff / abandon) + a `CallbackDeliveryResendService`
(ops resend) — supersedes the earlier "outbox-retry only, withdrawal callbacks deferred" note above.

**Scaffolding prune (2026-08-13) — DONE, build green (0 errors).** Removed **18 empty projects** (each held only a
`.csproj`, zero source): all **7 module `Api/` projects** (Wallet, Blockchain, Ledger, KeyManagement, Merchant,
Deposit, Withdrawal — HTTP endpoints live in the hosts under `Api/`, never in a per-module `Api/`), the empty
**`Events/` + `Workers/`** of Wallet · Blockchain · Ledger · KeyManagement · Merchant, and **`Blockchain/Application`**
(ports live in `Contracts`, adapters in `Infrastructure`) — plus a stray empty
`PaymentIntent/Infrastructure/Migrations/` folder (real migrations are under `.../Persistence/Migrations/`). Solution
is now **81 code projects (was 99)**; all 31 inbound `ProjectReference`s (both hosts + 7 Tests + Blockchain.Infrastructure)
were pruned via `dotnet remove reference` / `dotnet sln remove`. **Kept (NOT redundant):** the in-memory adapters +
`AddDevelopment*` seeders (deliberate dev/test DI seams).

**Money-edge dedup (2026-08-13, follow-on) — DONE, build green.** The duplicated `Money/AmountConversion` (the §14
display↔base-unit edge converter) was consolidated into **`SharedKernel.AmountConversion`** (next to `MoneyLimits`;
logic byte-identical — the superset `TryToBaseUnits` + `ToDisplay`; XML-doc'd API/UI-edge-only so Domain never does
decimal math). Both hosts' copies + their `Money/` folders were deleted; the 4 call sites resolve it via the
`SharedKernel` using they already had. Gave it its **first direct tests** in a new **`SharedKernel.Tests`** project
(18 tests: precision-floor, over-precision-rejected-never-truncated, non-positive-rejected, lossless round-trip across
6/9/18-dp) — SharedKernel's first unit-test project. Solution now **82 projects**.

**Fee declaration path + symmetric deposit-fee snapshot (2026-08-17) — DONE, full suite green.** Closed the real
gap that the [[hybrid-fee-model]] engine had no *write* path: `Merchant.SetAssetPolicy` (the only fee writer) had
**zero production callers** (tests only), so every merchant was unpriced → `FeeSchedule.None` → **0 fee on every
deposit and withdrawal** (the whole flat+% engine was correct but never invoked with a non-zero schedule; the
platform earned no fee revenue). Also fixed the deposit/withdrawal fee **asymmetry** the user flagged (withdrawal
snapshots its fee to a column; deposit had none — its fee lived only as a Ledger `FeeRevenue` posting line).
**Three parts:**
(1) **Declaration (the core):** new `Merchant.Application/IMerchantAssetPolicyService` (`SetFeesAsync` upsert +
`ListAsync`), backed by `IMerchantRepository`; validation delegated to the domain `FeeSchedule`. New **Admin-only**
Ops endpoints `PUT/GET /api/v1/ops/merchants/{id}/fees` (`OpsMerchantFeeEndpoints`) — the "fee declared in the UI"
surface; display↔base only at the edge (§14; a zero fixed component is valid = pure-% pricing). **v1 is fees-only**:
the policy's *limit* columns (`SweepThreshold`/`MinimumWithdrawal`/`MaximumWithdrawal`) are **recorded-but-unread**
today (withdrawal min/max come from config `WithdrawalPolicy`, sweep from `Sweep:Policies`), so the service
**preserves** existing limits rather than expose numbers the flows ignore — per-merchant limits + wiring them in are
a deferred Platform-UI follow-up.
(2) **Symmetric deposit fee (T3 — money-path + event-contract change):** `deposit.Deposit` gained a `Fee` column
(migration `AddDepositFee`, `60-deposit.sql` regenerated — no QUOTED_IDENTIFIER header, its filtered indexes are
EXEC-wrapped). The Deposit module now **computes the fee once at detection** (`DepositDetectionService` reads
`IMerchantFeeSchedule` — Deposit.Application now refs Merchant.Contracts, §4.5, exactly as Withdrawal does at
request), **snapshots it on the record**, and **carries it on `DepositConfirmed`/`DepositOrphaned`** (new
backward-compatible `FeeBaseUnits`; a pre-fee in-flight event ⇒ null ⇒ 0). The **Ledger consumes the event's fee**
instead of re-deriving (`DepositEventHandlers` no longer inject `IMerchantFeeSchedule`) — which also **fixes the
documented reorg-reversal drift** (the reversal now mirrors the exact fee charged, the "Withdrawal-symmetric"
fix the code's own comment named). Ledger split logic unchanged; the fee is data on the event, keeping the Ledger
chain/pricing-agnostic. Fee value is identical to before (same `QuoteDepositFee(received)`), only computed earlier.
(3) **Display:** `DepositSummaryView`/`WithdrawalAdminRow` gained `FeeBaseUnits`; the two Ops transaction screens now
emit the **real** fee (deposit fee is null until a deposit matches the invoice) — the hardcoded `fee = "0"`
placeholders are gone. **Dev:** `DevMerchantSeeder` now applies a sample `%` fee to every active asset (config
`Merchant:DevSeed:{DepositFeeBps,WithdrawalFeeBps}`, default 100/50 bps; idempotent upsert; `IAssetCatalog` resolved
softly so Merchant-only test hosts skip it) so the dev round-trip shows a non-zero split. **Deferred (agreed):**
per-merchant limits + enforcement (Platform UI), a config platform-default fee for unpriced merchants, and a filled
`Api.IntegrationTests`. New tests: `MerchantAssetPolicyServiceTests` + deposit fee snapshot/round-trip/reorg-fee-
reversal proofs.

**Merchant Withdrawal (earnings cash-out) — Phase 1 (money path) DONE, full suite green.** A SECOND, distinct
money-out kind: the merchant cashing out its own earnings, vs the existing user-payout `Withdrawal`. Kept strictly
separate from the user flow (the user flagged the earlier conflation): the two share the **identical execution
pipeline + ledger impact** (reserve `MerchantLiability` → sign → broadcast → confirm → settle; the Ledger never
learns the kind), so it's ONE aggregate with a **`WithdrawalKind {User, Merchant}`** discriminator, not a duplicate
module — the divergence lives only in the request layer, destination, and (future) reporting. **One shared balance:**
both kinds debit `MerchantLiability`; "earnings" = the residual, which also floats user payouts, so the cap is a
liquidity guard. Decisions locked with the user: merchant-initiated via **signed `POST /api/v1/merchant-withdraw`**
(no destination — resolved from a pre-registered **settlement wallet**, staff-whitelisted so a compromised API key
can't redirect earnings, §10); **charges the same withdrawal fee** as a user payout (platform still bears gas);
per-withdrawal **flat/% liquidity cap** = `min(flat, ⌊available·bps/10000⌋)`, unset ⇒ cash out up to balance.
**Domain:** `Withdrawal.Kind` (+`Request(kind)`); the idempotency index widened to **`(MerchantId, Kind,
MerchantTransactionId)`** so user/merchant reuse the same reference without colliding (migration `AddWithdrawalKind`);
new `MerchantSettlementWallet` entity (per `(MerchantId, Chain)`) + `MerchantAssetPolicy` cap columns
`MerchantWithdrawalFlatCap`/`MerchantWithdrawalPercentBps` — **distinct** from the user `Min/MaxWithdrawal` (migration
`AddMerchantSettlementAndCashOutCap`; `70-withdrawal.sql`/`20-merchant.sql` regenerated). **Contracts (§4.5):**
`IMerchantSettlementDirectory` + `IMerchantWithdrawalCap`. **`MerchantWithdrawalService`** resolves the settlement
address, applies the cap (reads `ILedgerQuery` balance only when a % cap is set — best-effort; the reserve stays the
atomic overdraw guard), charges `IMerchantFeeSchedule.QuoteWithdrawalFee`, and reserves. **Also fixed a latent bug:**
`WithdrawalRepository.IsIdempotencyViolation` still checked the stale index name `UX_Withdrawal_Idempotency` (renamed
to `UX_Withdrawal_MerchantTxn` long ago) — the concurrent-duplicate race path wasn't being caught. Dev:
`DevMerchantSeeder` now also registers a settlement wallet (`Merchant:DevSeed:SettlementAddress`) + optional cap
(`MerchantWithdrawalPercentBps`, default 0 = no cap). Tests: `MerchantWithdrawalServiceTests` (settlement-not-
registered, flat/%/min caps, dedup, fee, reserve-fail). **Phase 2 — DONE** (staff Ops endpoints to register the
settlement wallet + set the cap, and `Kind`-differentiated Ops reporting; see the merchant-admin Ops milestone below).
The merchant-facing `/transactions/query` **now looks up merchant cash-outs too** — an optional `kind` ("user" |
"merchant") narrows the withdrawal side (a user payout and a cash-out can share a reference under the `(merchant, kind,
reference)` key), auto-detecting deposit → user payout → cash-out when omitted; cash-outs return the additive
`type:"merchant_withdraw"` while the frozen user `type:"withdraw"` shape is unchanged (`IWithdrawalDirectory.FindByMerchantReferenceAsync`
gained a `kind` param; tests: `WithdrawalDirectoryTests`). Only a **paged history list** (browse all records, not a
single lookup) stays deferred — to be applied uniformly across every transaction-record endpoint at once.

**Settlement period (T+N) + merchant freeze (A1 money-path + domain) — DONE, full suite green.** Two admin controls
added ahead of Merchant-Withdrawal Phase 2. **(1) Settlement period (T+N):** a per-merchant `SettlementDelayDays`
(int, default 0 = T+0; on `Merchant`, migration `AddMerchantSettlementDelay`, `20-merchant.sql` regenerated) gates a
**second, tighter balance — "settled/withdrawable"** — applied to **BOTH** user payouts and the merchant cash-out
(user chose gate-both). A deposit confirmed on UTC calendar day *D* matures at `00:00Z` of *D+N*; cutoff =
`StartOfUtcDay(now).AddDays(1−N)`, a deposit is unmatured iff its journal `CreatedAt ≥ cutoff`. New
**`ILedgerQuery.GetMerchantSettledBalanceAsync(merchant, asset, cutoff)`** = `max(0, cacheBalance −
UnmaturedNetDeposits)`, where `UnmaturedNetDeposits = Σ(MerchantLiability lines of Deposit/DepositReversal journals
dated ≥ cutoff, credit−debit)` — computed as *total minus still-unmatured inflows* so releases/reversals/fees folded
into the cache stay correct and a deposit + its reorg-reversal (both recent) **net to zero** (the money-critical
subtlety; proven by `LedgerQueryTests`). A shared `SettledBalanceGate` (Withdrawal.Application) resolves the cutoff;
both money-out services **reject** an over-settled amount with `WithdrawalErrors.ExceedsSettledBalance`. The merchant
cash-out's **% cap now applies to settled** (not total). **T+0 is a deliberate no-op** — the gate is skipped and the
ledger reserve stays the sole balance guard (unchanged `InsufficientBalance` semantics for the common case). The
settled check is **best-effort**; the reserve remains the atomic overdraw guard on total balance (settled ≤ total).
A hard atomic settled ceiling (teach the reserve to net unmatured deposits) is a documented follow-up. **(2) Freeze:**
the merchant lifecycle value `Suspended` was **renamed to `Frozen`** (`Merchant.Suspend()→Freeze()`,
`IMerchantRegistrar.SuspendAsync→FreezeAsync`; migration also rewrites any stored `'Suspended'→'Frozen'`). It's the
existing `CanTransact` gate (blocks deposit-address requests + user payouts + cash-out); **`Activate` unfreezes** (the
existing Ops `PATCH .../status` `active:false/true` toggle now freezes/unfreezes — no new endpoint). **Money-correctness
(§14):** a frozen merchant's on-chain deposits **still credit the ledger** — freeze stops issuing/withdrawing, never
recording (no reconciliation drift). `SettlementDelayDays` surfaced on `MerchantSummary` (Contract) + `MerchantAdminView`.
Dev: `Merchant:DevSeed:SettlementDelayDays` (default 0). The staff Ops setter now exists (see the merchant-admin Ops
milestone below).

**Merchant-admin Ops surface (Phase 2 — settlement period / wallet / cap setters + `Kind` reporting) — DONE, full suite
green.** The staff write paths that were dev-seed-only are now real Admin endpoints in `Api/OperationsApi` (T2, over
domain methods already built + tested; no schema change). **Setters (`OpsMerchantSettlementEndpoints`, all `.RequireAdmin()`):**
`PUT /ops/merchants/{id}/settlement-period` (→ `IMerchantRegistrar.SetSettlementDelayAsync`), `PUT .../settlement-wallet`
(→ `SetSettlementWalletAsync` — whitelists the cash-out destination, §10), `PUT .../withdrawal-cap`
(→ `IMerchantAssetPolicyService.SetMerchantWithdrawalCapAsync`, flat display→base at the edge §14, null flat = no flat cap).
**Read-back (no new endpoints):** `GET /ops/merchants/{id}` now returns `settlementDelayDays` + `settlementWallets`
(added to `MerchantAdminView`), and `GET /ops/merchants/{id}/fees` returns the cap alongside fees (added to
`MerchantAssetPolicyView`). **`Kind` reporting:** `WithdrawalAdminRow` gained `Kind` ("User"/"Merchant") + `WithdrawalAdminFilter`
a string `Kind` filter (Contracts stay Domain-free — parsed to the enum in the directory); the Ops withdrawal screen
(`OpsWithdrawalTransactionEndpoints`) takes a `kind` query param and emits `kind` per row, so user payouts and merchant
cash-outs are now distinguishable/filterable (was hardcoded `type:"withdrawal"`). Tests: `MerchantAssetPolicyServiceTests`
cap round-trip/preserve-fee/invalid-bps. (The per-merchant withdrawal **approval threshold** was since built — see the
per-merchant-approval-threshold milestone below; the merchant-facing cash-out **lookup** in `/transactions/query` too — see the
`kind`-aware query note in the Merchant-Withdrawal milestone above; a paged history list across all transaction-record
endpoints remains the deferred piece.)

**Per-merchant user-withdrawal min/max (enforced) + platform-default fee (T3) — DONE, full suite green.** Two money-path
features. **(1) Per-merchant min/max, now ENFORCED (not just recorded):** `MerchantAssetPolicy.MinimumWithdrawal` became
**nullable** (like `MaximumWithdrawal`) so `null` = unset ⇒ the flow uses the platform config limit and a set value fully
overrides (raise OR lower) — the user's "config default, admin overrides per merchant". Migration `AddNullableWithdrawalMinimum`
(alters the column + rewrites the two CHECK constraints to be NULL-aware + backfills existing `0`→`NULL` as "unset";
`20-merchant.sql` regenerated). `WithdrawalRequestService` (USER flow only — cash-out uses the cap, not min/max) now reads a new
Merchant Contract **`IMerchantWithdrawalLimits`** and gates on `effectiveMin = merchantMin ?? configMin` / `effectiveMax =
merchantMax ?? configMax`. Setter: `IMerchantAssetPolicyService.SetWithdrawalLimitsAsync` + Admin `PUT /ops/merchants/{id}/withdrawal-limits`
(null = unset; 0 = explicit "no min"); read-back on `GET .../fees` (`minimumWithdrawal`/`maximumWithdrawal` added to
`MerchantAssetPolicyView`). Fee/cap setters now leave min **unset** (null) instead of 0, so pricing a merchant no longer
silently forces a 0 minimum. **(2) Platform-default fee for unpriced merchants:** config `Merchant:DefaultFee`
(`{DepositFeeBps, WithdrawalFeeBps}`, **percentage-only** — a platform-wide flat is meaningless across assets; 0/0 = no
default) resolved once into a `MerchantDefaultFee` holder; `MerchantFeeSchedule` substitutes it whenever a merchant's
resolved schedule is `None` (no explicit fee) — so an unpriced merchant is never silently free, while ANY explicit fee
(even partial) suppresses the default. Flows through deposit gross-up + withdrawal + the Ledger split unchanged (they all
resolve via `IMerchantFeeSchedule`). **Account opening stays free** (no onboarding fee anywhere). Tests:
`WithdrawalRequestServiceTests` (override raise/lower/config-fallback), `MerchantDefaultFeeTests` (config→schedule, zero/
invalid→None), `MerchantPersistenceTests` (unpriced→default, explicit-overrides-default, no-config→free),
`MerchantAssetPolicyServiceTests` (limits round-trip/preserve-fee/min>max). **Deferred:** per-asset flat default fees;
the "explicitly zero-rated" merchant (a fully-zero explicit fee is indistinguishable from unpriced, so it also gets the
default — a future zero-rated flag if needed).

**Per-merchant withdrawal approval-threshold override (enforced, both kinds) — DONE, full suite green.** The last
platform-wide withdrawal knob (fees/min-max/caps/settlement/freeze were already per-merchant) is now a per-(merchant,
asset) override of the config `Withdrawal:Policies:{Chain}:ApprovalThresholdBaseUnits`. Rule: `effectiveThreshold =
merchantThreshold ?? configThreshold`; a payout `amount > effectiveThreshold` needs human oversight. **Nullable ⇒
unset ⇒ config** (existing merchants unaffected). `MerchantAssetPolicy` gained a nullable `ApprovalThreshold` column +
`SetApprovalThreshold` (migration `AddMerchantApprovalThreshold`, `20-merchant.sql` regenerated; a NULL-aware `>= 0`
CHECK). **Applies to BOTH withdrawal kinds at all three enforcement points** — the key correctness point, since the
config threshold drove two gates that had to move together or a raised threshold would auto-approve at request yet be
second-gated at processing: `WithdrawalRequestService` (user → PendingApproval), `MerchantWithdrawalService` (cash-out
→ PendingApproval), and `WithdrawalProcessingService` (the "large = manual **release**" AwaitingRelease gate). New
**focused `Merchant.Contracts/IMerchantApprovalThreshold`** (deliberately NOT folded into the user-only
`IMerchantWithdrawalLimits` — the threshold applies to both kinds), read by all three services (Withdrawal.Application
already refs Merchant.Contracts). Setter `IMerchantAssetPolicyService.SetApprovalThresholdAsync` + **Admin** `PUT
/api/v1/ops/merchants/{id}/approval-threshold` (display→base at the edge §14; null = unset, 0 = "everything needs
approval") + read-back on `GET .../fees`. **Security (§10):** Admin-only setter — a compromised merchant API key can't
lower its own approval bar; the ledger reserve stays the atomic balance guard; the threshold is a best-effort policy
control at request + processing (a threshold change in the near-zero window between a payout's request and its
processing is a documented edge, same class as the fee-mid-reorg note). Tests: domain set/unset/preserve/negative
(`MerchantAssetPolicyServiceTests`) + enforcement (`WithdrawalRequestServiceTests` user PendingApproval/auto-approve,
`MerchantWithdrawalServiceTests` cash-out PendingApproval). **Deferred:** snapshotting the threshold on the withdrawal
at request (to avoid the near-zero mid-flight re-resolution) — not needed for the common case.

**Energy + Sweep Ops read surface (`Api/OperationsApi`) — BUILT, full suite green (685 passed, 8 expected skips).**
Closed the last back-office blind spots (Reconciliation/Treasury already had theirs): an operator can now see the
gas-hub + concentration paths that previously lived only in logs + SQL. All pure **reads** over the state machines the
money host owns — this host runs no scan/monitor/stake/sign/broadcast worker and holds no keys (§4.7). Three new
endpoints, each gated on a new permission (Admin's `WildcardPermission` passes; codes assignable in the Roles UI):
`GET /api/v1/ops/sweeps` (`ops.sweep.view`) — the sweep state machine (deposit → cold treasury), filter by
chain/status/wallet, paged, with a by-status summary; `GET /api/v1/ops/energy/operations` (`ops.energy.view`) — the
stake/delegate/top-up operations, filter by chain/kind/status/staking-wallet, paged + summary; `GET
/api/v1/ops/energy/resources` (`ops.energy.view`) — per-wallet resource-health snapshots (energy/bandwidth,
Healthy/Low/Critical, frozen/available TRX), **worst-health-first**. Amounts cross to display at the edge (§14) — a
display value **plus** the exact base-unit integer string (audit needs the precise integer); TRX shows with 6dp via a
documented constant (TRX is deliberately NOT in the deposit catalog, so no catalog lookup). **New read seams (additive,
Contracts-only §4.5):** Sweep gained its **first `Contracts` project** — `ISweepDirectory` (+ `SweepAdminRow`/
`SweepAdminFilter`), backed by a no-tracking `SweepDirectory` over `SweepDbContext`; Energy gained
`IEnergyOperationDirectory` (+ row/filter) over `EnergyDbContext`, and `IWalletResourceStore.ListAsync` for the Mongo
resource read (consumed directly like Reconciliation's store). **Composition mirrors `AddReconciliationReadModel`:** new
`AddSweepReadModel(conn)` + `AddEnergyReadModel(config, conn)` register ONLY the DbContext + read directory (+ the Mongo
resource store) — NOT `ResourceMonitorService`/`StakingService`/`EnergyDelegationService`/`SweepScanService` or their
workers, which need chain/signer capabilities this host lacks and must never run (§4.7). **No schema/migration/ledger/key
change.** Host-boot verified (DI graph validates, Kestrel up, all three routes in the Swagger doc). Tests:
`SweepDirectoryTests` + `EnergyOperationDirectoryTests` (real SQL Server — order/filter/summary + exact decimal(38,0)
amount round-trip beyond Int64). **Deferred:** these are read-only (no ops *action* on Energy/Sweep — e.g. manual
retry/cancel of a stuck sweep, or a manual stake trigger — is a later T3); a paged history across all transaction-record
endpoints remains the deferred cross-cutting piece; `EnergyPolicy` (thresholds) has no read endpoint yet (the resource
snapshot already carries target/minimum energy).

**httpOnly cookie + CSRF session auth for `Api/OperationsApi` (browser-safe staff login) — BUILT, full suite green
(685 passed, 8 expected skips), HTTP-verified end-to-end.** Unblocks the Admin UI's target auth (its `CLAUDE.md` D7 /
§12 / blocker #1): the backend was pure `Authorization: Bearer`, so a browser had to hold the session token in JS
(sessionStorage = XSS-exposed). The staff session was already a **server-side opaque token** (256-bit random, only its
SHA-256 hash stored on `StaffSession`, permissions snapshotted at login), so this is a *delivery* change, not a new trust
model — the same opaque token now also rides in an **httpOnly cookie**. **Additive, not a replacement:**
`StaffBearerAuthMiddleware` accepts the token from EITHER the `Authorization: Bearer` header (non-browser clients + the
UI's interim bearer mode — kept working) OR the `cpe_ops_session` cookie; one `/auth/login` serves both (returns `token`
for bearer mode AND sets the cookie + returns `csrfToken`). **CSRF = synchronizer token bound to the session:** new
`StaffSession.CsrfToken` (per-session CSPRNG, migration `AddStaffSessionCsrfToken`, `db/sql/100-identity.sql`
regenerated), returned in the **login + `/auth/me`** bodies (JS-readable — useless without the httpOnly cookie), echoed by
the SPA as `X-CSRF-Token`, and **enforced only on cookie-authenticated unsafe methods** (POST/PUT/PATCH/DELETE),
`FixedTimeEquals`-compared; a bearer-header request is not ambient ⇒ inherently CSRF-safe and exempt (safe methods never
need it). **CORS:** credentialed allow-list from config `Cors:AllowedOrigins` (exact origins, never `*`; `AllowCredentials`;
`UseCors` before the auth middleware; OPTIONS preflight bypasses auth) — empty ⇒ same-origin-only (safe default); dev seeds
the Vite origins. **Cookie attributes:** `HttpOnly` always; `Secure` defaults `!Development` (so http://localhost works),
config-overridable; `SameSite` defaults **Lax** (correct for dev + a same-registrable-domain UI/API), config-settable to
`None` (auto-forces Secure) for a cross-site split — the one deployment knob (`Auth:Cookie`). **No ledger/key impact.**
HTTP-proven on a booted host: login sets `Set-Cookie: …; samesite=lax; httponly`; cookie-only auth works on `/auth/me` +
the gated `/ops/sweeps`; cookie POST **without** `X-CSRF-Token` → **403**, **with** it → 200; logout revokes + clears the
cookie; bearer path still works and is CSRF-exempt; CORS preflight + credentialed headers correct, unknown origin refused.
Tests: `StaffAuthServiceTests` gains CSRF-token issuance/validation assertions. **EF-tooling note (net10):** `dotnet ef`
here needs `DOTNET_ROLL_FORWARD=LatestMajor` (its host is net8, the assemblies net10) — see [[ef-tooling-net10-rollforward]].
**Deferred:** a machine-readable `errorCode` alongside `error` (the UI can't branch on prose — UI blocker #6); sliding-
expiry / refresh (session is fixed-TTL, re-login on expiry); per-deployment prod `Cors:AllowedOrigins` + `Auth:Cookie`
(config only, not code).

**Merchant Portal API — Phase 1 (tenant-scoped auth spine + read screens) — BUILT, full suite green (689 passed,
8 expected skips), HTTP-verified end-to-end.** The merchant-facing back-office had **no backend** (MerchantGateway is
machine-to-machine HMAC only — no merchant login/session, per the UI's §9.2). Phase 1 (of the confirmed 3-phase plan)
builds the foundation from a Vue template (`Downloads/MerchantBO`, mock-auth prototype): a **new tenant identity module**
+ a **new host**, delivering login + the reusable read screens. **New module `Platform/MerchantIdentity`** (schema
`merchantidentity`, migration `InitialMerchantIdentity`, `db/sql/105-merchantidentity.sql`): `MerchantUser` (global-unique
username → resolves one tenant; PBKDF2 password + opaque session token via the shared SharedKernel primitives
`Pbkdf2PasswordHash`/`OpaqueToken`, while each port stays module-owned so the two identity modules stay independent, §4.5)
+ `MerchantUserSession` (opaque server-side token, only its SHA-256 hash stored,
revocable/fixed-TTL, **carries the tenant `MerchantId`** + a per-session CSRF token). `MerchantAuthService` mirrors
`StaffAuthService` but binds every session to one `MerchantId`; Phase 1 grants all portal users `["*"]` (a per-merchant
role model is Phase 2). **New host `Api/MerchantPortalApi`** (third composition root) behind
`MerchantSessionAuthMiddleware` — the SAME httpOnly-cookie + CSRF model as the Ops host (`cpe_portal_session` cookie,
`X-CSRF-Token` on cookie-authenticated writes, bearer accepted + CSRF-exempt, credentialed CORS allow-list). **The
non-negotiable spine — tenant isolation:** the `MerchantId` comes ONLY from the validated session
(`PortalTenant.MerchantId`), never from a request param/body, so a merchant cannot address another merchant's data — and
an endpoint doesn't name a merchant at all. **Read endpoints, all tenant-scoped** (`/api/v1/portal/…`): `auth/{login,
logout,me}`, `profile` (`IMerchantDirectory`), `funds` (`ILedgerQuery` available + settled, the two balance kinds kept
distinct §14), `fees` (`IMerchantAssetPolicyService`), `addresses` (`IWalletDirectory.ListAssignedWalletsAsync`),
`transactions/{payin,payout,cash-out}` (`IPaymentIntentDirectory` + `IDepositLookup` enrich; `IWithdrawalDirectory` by
`Kind` User/Merchant). Composes the module read-Contracts read-only (§4.7 — no workers; in-memory chain/signer stubs in
dev only to satisfy Withdrawal→Treasury→allocator DI, never signs). Dev: `AddDevelopmentMerchantPortalSeed` binds a login
(`merchant001`, config `MerchantIdentity:DevSeed`) to the seeded `DEVMERCHANT`. **HTTP-proven on a booted host:** login
sets the httpOnly cookie + returns `csrfToken` + `merchantId`; cookie-only auth works on every read; `/profile` + `/funds`
return the SESSION's merchant (USDT balance, real TRON deposit address); a **`?merchantId=<other>` injection is ignored**
(still the caller's own tenant); cookie POST without `X-CSRF-Token` → 403, with → 200; logout revokes; unauth → 401. Tests:
`MerchantAuthServiceTests` (4 — login issues a tenant-bound + CSRF session, validate surfaces the tenant, wrong-password
== unknown-user, expiry). **Phase 2 — DONE** (see the next milestone). **No backend, not built**
(the §9-style gaps, filed): dashboard (no aggregate), risk-events, reconcile (platform-only custody audit), reports, 2FA
(the portal's login accepts an `otp` field but IGNORES it — 2FA is unimplemented).


**Merchant Portal API — Phase 2 (merchant RBAC + write actions) — BUILT, full suite green (726 passed, 8 expected
skips), HTTP-verified end-to-end.** Turns the read-only Phase 1 portal into a working back-office: merchants now
manage their own staff accounts and roles, rotate their API credential, maintain their IP allowlist, and submit
money-out. **RBAC came first on purpose** — without it every portal user held the Phase 1 wildcard, so shipping
writes first would have given every merchant user the ability to move money. **MerchantIdentity gained roles:**
`MerchantRole` (per-tenant — unique on `(MerchantId, Name)`, so two merchants may both have a "Finance"; a role
never crosses tenants), `MerchantUser` gained a **nullable** `RoleId` + `MustChangePassword` (migration
`AddMerchantRolesAndAccountLifecycle`, `db/sql/105-merchantidentity.sql` regenerated). **Null role is fail-closed** —
no role ⇒ an EMPTY permission set at login, so an account can sign in and do nothing; never an implicit grant. The
session snapshots the role's codes at login (a role change takes effect next login — the staff module's same
trade-off). New `MerchantRoleService` + `MerchantAccountService`, both taking the caller's `merchantId` as their
first argument and filtering every repository query by it, so a foreign id reads as "not found" rather than being
actionable. Account creation/reset issue a **generated one-time password** (never admin-chosen), returned once;
`ChangeOwnPasswordAsync` requires the current password and clears the forced-change flag. Lock-out guards mirror the
staff module: never disable yourself, never disable the tenant's last active account, never delete a role still in
use. **Host:** new `PortalPermissions` catalog (`portal.<module>.<verb>`, host-owned §4.5) + `RequirePortalPermission`
gate; **every** Phase 1 read is now gated too. Endpoints: accounts CRUD + status/role/reset-password,
`account/change-password` (ungated — everyone may change their own), roles CRUD + permissions, `GET /permissions`,
`api-credential` (metadata + rotate), `allowed-ips` (IP/CIDR validated at the edge), and money-out `payouts` /
`cash-outs`. **Money-out calls the SAME Application services the HMAC `MerchantGateway` uses** — the service layer is
the money boundary, not the host — so idempotency on the merchant reference, the per-merchant fee, the settled (T+N)
gate, the liquidity cap, the ledger reserve as the atomic overdraw guard, and the approval threshold all still apply
unchanged; the host adds only display→base conversion (refusing over-precision, §14) and the 409-on-duplicate mapping.
The portal host gained `Withdrawal:Policies` config (it now submits requests, so it reads the same per-chain
limits/threshold the money host does; it still registers no workers, so it never processes/signs/broadcasts, §4.7).
**Two security decisions, both deliberate:** (1) **the settlement wallet is NOT merchant-editable** — it stays a
staff-only Ops action precisely so a compromised merchant credential or portal session cannot redirect earnings (§10);
(2) **portal-created user payouts were approved by the user with the risk stated** — a payout sends to a
request-supplied address, so a stolen browser session is a wider blast radius than the server-side HMAC path; it is
contained by its own permission code (`portal.payouts.create`, off unless a merchant admin grants it) and by the
approval threshold, which still forces staff review above the configured amount. A cash-out carries no such risk
(staff-whitelisted destination). Role codes are validated against the portal catalog at the edge, so a tenant cannot
store an unknown code — **including a platform `ops.*` code** (HTTP-proven: refused 400). **HTTP-proven on a booted
host:** an admin created a `Finance` role holding only `portal.overview.view` and an account bound to it; that user's
session carried exactly that one code with `mustChangePassword: true`, and was allowed `/profile` (200) but refused
`/transactions/payin`, `/accounts`, `/api-credential`, `POST /payouts`, and `POST /roles` (all 403 — it cannot
self-promote or reach money-out); allowed-IPs rejected a malformed entry (400) and accepted an IP + CIDR; CSRF still
enforced on every Phase 2 write (403 without the header); money-out surfaced real business rejections, not crashes
(below-minimum, insufficient balance) and refused an over-precision amount; a `?merchantId=` injection on an
account-list write surface was ignored. Tests: `MerchantAuthServiceTests` (8 — role codes resolved onto the session,
no-role ⇒ empty, disabled account refused) + new `MerchantTenantIsolationTests` (9 — merchant A cannot read/edit/
delete/reset merchant B's roles or accounts even knowing the exact id, a foreign role can never be assigned at
creation or later, same-name roles across tenants, in-use role undeletable, self/last-account disable guards,
change-own-password rules, global username uniqueness). Also folded the temp-password generator into a shared
`SharedKernel.TemporaryPassword` primitive rather than adding a third copy. **Deferred:** 2FA (still unimplemented
backend-wide — the login `otp` field remains ignored), a portal audit log of merchant-admin actions, and revoking a
disabled account's live sessions (today it is refused at next login, mirroring the staff module).

**Two-party payout approval (merchant approver → platform staff) + single-approval platform gate — BUILT, full
suite green (731 passed, 8 expected skips), HTTP-verified.** Closes the gap between the intended payout flow and
what Phase 2 shipped. **Intended flow, now implemented:** a merchant portal user submits a payout → it waits in the
new **`PendingMerchantApproval`** state for the merchant's OWN approver → on approval, at/below the effective
approval threshold it is cleared to send automatically; above it, it moves to `PendingApproval` and waits for
platform staff. **A merchant can never approve past the platform gate** — the routing is re-resolved server-side
against the threshold at approval time, not chosen by the merchant. **Scope: portal-initiated payouts only.** An
HMAC-API payout passes `RequiresMerchantApproval: false` (the default) and behaves exactly as before — the
merchant's own server already authorised it by signing the request, so the frozen API contract is untouched
(regression-tested). **Separation of duties comes from the permission split, not a different-user rule:** approving
requires a distinct `portal.payouts.approve` code, so a user who may only submit cannot also sign off; a merchant
admin holding both may approve a payout they raised (which keeps one-person merchants able to pay out). **Merchant
rejection releases the ledger reserve** via the same event path a platform rejection uses — a decline can never
strand the merchant's money in clearing (asserted on the balances). **Also fixed a real double-gate defect:**
`Withdrawal.Approve()` now stamps `ReleasedAt`/`ReleasedBy`, because an explicit staff approval IS the release.
Previously an above-threshold payout tripped BOTH the request-time `PendingApproval` gate and the processing-time
`AwaitingRelease` gate on the *identical* threshold, so staff had to approve on one Ops screen and then release on
another for a single payout — a leftover from when `AwaitingRelease` was built during the hot-wallet float work as
the only human checkpoint (payouts then arrived only via API with no human at request time), never reconciled when
request-time approval landed on the same threshold. The release path is unchanged for what it is actually for —
resuming a payout parked as `AwaitingFunds` for insufficient hot-wallet float — and the `ReleasedAt is null` check
still backstops anything reaching `Approved` without human review (e.g. a threshold lowered mid-flight).
**Schema:** new status value only (string-stored; `PendingMerchantApproval` is 23 chars, fits the existing
`nvarchar(24)` — no column change) plus two nullable audit columns `MerchantApprovedBy`/`MerchantApprovedAt`
(migration `AddMerchantPayoutApproval`, `db/sql/70-withdrawal.sql` regenerated). **No ledger-impact change** — the
reserve still happens at request; only *where the payout waits* changed. New `IMerchantPayoutApprovalService`
(tenant-scoped: another merchant passing a real withdrawal id gets "not found", never a silent no-op) + portal
`POST /payouts/{id}/{approve,reject}`; the Ops directory surfaces `pending_merchant_approval` so staff can see —
but not action — a payout still awaiting the merchant. Tests: 5 new pipeline tests in `WithdrawalFlowTests`
(below-threshold sends on merchant approval alone; above-threshold needs merchant THEN platform and the worker
moves nothing in between; rejection returns the reserve; cross-tenant approval refused; API payout unaffected) and
the old "held for operator release" test rewritten to assert the single-approval behaviour. HTTP-proven: the
approve/reject routes are gated on `portal.payouts.approve` (a user with only `portal.overview.view` gets 403), an
unknown/foreign withdrawal id returns 404, and the permission catalog advertises the new code. **Deferred:** a
merchant-side notification when a payout is waiting for their approval (today the portal must poll the payout list).
**Admin-UI search gaps (Crypto.UI REQ-12/13/15) — BUILT, full suite green (742 passed, 8 expected skips).** Three
read-only, additive filter/field gaps the frontend filed against the Ops API. **No schema, migration, ledger, key,
or module-boundary change** — every seam is an optional parameter on an existing Contract with a default, so no
existing call site moved. **(1) REQ-15 — `status` filter on both transaction searches (the one with real
operational bite):** without it the settlement queue could only find the outstanding work that happened to land on
the page an operator loaded. Both searches now filter on the **effective** status — the collapsed vocabulary the
rows already report, not the domain enum — pushed into SQL so `totalCount` reflects it. Withdrawal:
`pending`(=Reserving/Approved/Signing/Broadcast) | `pending_merchant_approval` | `pending_approval` |
`insufficient_balance` | `awaiting_release` | `confirmed` | `failed`(=Rejected **and** Failed); the mapping lives
in `WithdrawalDirectory.DomainStatusesFor` **immediately beside `EffectiveStatus`**, because the two drifting apart
would mean a filter that silently excludes rows the same screen labels with that exact status. Deposit
(`PaymentIntentDirectory`): `pending`|`confirmed`|`expired`|`failed`, where `expired`/`pending` are **time-derived**
— a lapsed-but-not-yet-swept invoice is still `Waiting` in the DB but already reads expired, so the filter compares
`ExpiresAt` against the same clock the projection uses, captured once per request. An unknown value is a **400** at
the host and matches **nothing** in the directory (never everything — an unfiltered set shown as a filtered queue is
the dangerous failure). Vocabularies published as `WithdrawalEffectiveStatuses`/`PaymentIntentEffectiveStatuses` in
**Contracts** (not Infrastructure) so a host can validate without reaching past the boundary (§4.5). **(2) REQ-13 —
`walletType` filter on `GET /ops/wallets`** (`WalletAdminFilter.WalletType`, applied in the repository; unknown ⇒
400). **(3) REQ-12 — `coin` + `decimals` on ledger rows** (`GET /ops/transactions`): amounts deliberately stay exact
base-unit integers (§14 — the ledger never rounds), but a consumer couldn't format them without a second catalog
lookup; resolved once per distinct asset, and **null for a gas-denominated journal** (§5c `GasCost` — the gas
`AssetId` is deliberately outside the deposit catalog, so a consumer must render raw base units rather than guess a
precision). Tests (11 new, real SQL Server): the load-bearing one on both directories asserts that for **every**
status in the published vocabulary, filtering by it returns exactly the rows the unfiltered search labels with it —
the drift guard — plus a lapsed-invoice clock test, unknown-value-matches-nothing, kind×status AND-ing, and
wallet-type filter/count/AND-with-status. **Still open from that list:** REQ-7 (machine-readable `errorCode`),
REQ-6 (single-record detail endpoints), REQ-3 (dashboard aggregates), REQ-5 (`userId`/`payerAddress`, still
hardcoded null — needs a product decision: populate or drop the columns), and REQ-8's 13 unbacked screens. **Also
stale in `Crypto.UI/docs/backend-requirements.md`:** REQ-4 (merchant portal session API) and REQ-14 (policy
read-back) are **already delivered** — the UI team is holding `apps/merchant` behind REQ-4 without knowing.

**Admin-UI medium gaps (Crypto.UI REQ-3/5/6/7) — BUILT, full suite green (746 passed, 8 expected skips), HTTP-verified
on a booted host.** The second batch of frontend-filed gaps, all read-only/additive; **no schema, migration, ledger, key,
or money-path change.** **(1) REQ-7 — machine-readable `errorCode`:** every failure response now carries a stable dotted
code alongside the human `error` string (the UI's §25 forbids pattern-matching display prose, so it previously could not
branch on *why* something failed). Domain failures use the module's own `Error.Code` (`wallet.not_found`,
`withdrawal.duplicate_reference`, …) — which already existed and was simply being thrown away at the edge; host-level
validation/auth uses a new published `OpsErrorCodes` catalog (`ops.invalid_status`, `ops.permission_denied`,
`ops.csrf_invalid`, …). Delivered via a new **`OpsResults`** — the §7.1 "one mapper per host" that was missing: there
were **8 near-duplicate private `Fail(Error)` helpers that had already drifted apart on status codes** (Wallet/PaymentIntent
mapped non-NotFound→409, MerchantFee/Settlement→400, five others used the full switch). Now one canonical mapping
(NotFound→404, Conflict→409, Unauthorized→401, else 400); the drift is corrected in passing — the only behaviour change
is Conflict-typed errors on the MerchantFee/Settlement paths now correctly returning 409 instead of 400. Success
envelopes gained `errorCode: null` so the shape never changes between paths. **(2) REQ-6 — single-record detail
endpoints:** `GET /ops/transactions/{deposits,withdrawals}/{systemOrderNumber}` + `GET /ops/wallets/{id}`. Each returns
the **identical row shape** the list returns, because the row projection was extracted into a shared `BuildRowsAsync`
that both call — a separately written detail projection is precisely how a field ends up formatted one way on the table
and another on the record it opens. No new Contract methods for deposits/withdrawals (`SystemOrderNumber` was already a
unique narrowing on the existing filter); Wallet gained an additive `WalletAdminFilter.WalletId`. Miss ⇒ 404
`ops.not_found`. **(3) REQ-5 — `userId`/`payerAddress` dropped** (user's call): both were hardcoded `null` on every
deposit/withdrawal row, so they were noise; if real user attribution is wanted later it gets added deliberately as a
populated field, not a null placeholder. **(4) REQ-3 — dashboard aggregates** (`GET /ops/dashboard`, any staff session,
no permission gate — gating the landing page would hand a new account a blank screen). Built the `operational` block
(the UI explicitly said "if only one block can be built, build operational") + `custody` (free — reuses the
reconciliation snapshots). Every key maps 1:1 onto a filter the UI can link to, using the same effective-status
vocabulary, so a tile and the list it opens **cannot disagree** — enforced by test, not convention. Costs: grouped SQL
COUNTs (new `IWithdrawalDirectory.GetStatusCountsAsync`, folding domain statuses into the effective buckets via the same
mapping; new `ICallbackDeliveryQuery.GetStatusCountsAsync`) + two already-derived Mongo snapshot reads — not scans of
transaction history. **A real defect found and fixed while verifying:** the dashboard hard-failed (500) when Mongo was
down, hiding the SQL-backed withdrawal work queue that was perfectly healthy — unacceptable for a landing page whose
Mongo inputs are *derived observability, never money truth* (§2). It now degrades: 200 with `operational` intact, the
Mongo-derived counts **`null` not `0`** (a fake 0 drift reads as "all balanced" — the worst thing to show on a custody
tile), `custody: []`, and explicit `custodyAvailable`/`energyHealthAvailable` flags. The dedicated `/ops/reconciliation`
screen deliberately still fails loudly — there, "the custody audit is down" IS the answer to the question asked.
**`volume` deliberately NOT built** (returns `[]`): a per-asset windowed breakdown needs a purpose-built grouped
aggregate, because the existing totals path folds BigInteger money client-side by design (no SQL SUM translation for
this project's money mapping, §14) — a naive 30-day version would table-scan the landing page, the one thing REQ-3's
own acceptance criteria rule out. **HTTP-proven:** all 4 new routes in the Swagger doc; every `ops.*` validation code
returned correctly (10 cases); 404+code on all three detail endpoints; `ops.unauthenticated`/`ops.invalid_credentials`/
`ops.csrf_invalid` on the auth paths; the degraded dashboard returning 200 with nulls + false flags. **NOT verified
live:** the Mongo-populated custody/energy happy path (Docker was not running locally, so no Mongo) — the degraded path
is the one exercised. Tests: +5 (withdrawal status-counts agree with the filtered search for every status, folded
buckets sum correctly; wallet by-id and unknown-id). **Still open from that list:** REQ-8's 13 unbacked screens
(product decisions) and `volume`. **Stale in `Crypto.UI/docs/backend-requirements.md`:** REQ-4 (merchant portal
session API) and REQ-14 (policy read-back) are **already delivered** — the UI team is holding `apps/merchant` behind
REQ-4 without knowing.

**Mongo dev environment repaired + the REQ-3 Mongo path verified (2026-08-26) — two real config defects found.** The
native MongoDB 8.3.8 install on this machine cannot run at all: `mongod.exe --version` exits `0xC0000139`
(STATUS_ENTRYPOINT_NOT_FOUND), i.e. a load-time import the OS does not export, so the service times out after 30s
without ever writing a log (Event ID 7000/7009; MSI Error 1920 during install). **Not** AVX (Comet Lake i7-10610U has
AVX2, and that would be 0xC000001D), **not** ACLs (NetworkService has FullControl on data+log), **not** PATH DLL
hijacking (identical failure with a minimal PATH) — an 8.3 rapid-release vs Windows 10 19045 mismatch. **Resolved by
downgrading to MongoDB 8.2** (`8.2.12`), which runs correctly; **dev and local staging now run Mongo NATIVELY on
localhost:27017, not in Docker** (compose's `mongodb` service is a fallback only — running both contends for 27017,
and the loser dies with `Error setting up transport layer`, which for the Windows service presents as that same silent
30s timeout; that port conflict was in fact why the 8.2 service also would not start until Docker was brought down).
Starting the service needs an elevated shell (`net start MongoDB`); non-elevated fails with `Cannot open MongoDB
service on computer '.'`. Note `mongosh` ships with **neither** the Server MSI nor Compass — it is a separate download
(installs to `%LOCALAPPDATA%\Programs\mongosh\`, **not** on `PATH`), so `db/mongo/00-bootstrap.js` cannot be applied on
a fresh machine until it is installed (not fatal: collections auto-create on write; the bootstrap only adds validators
+ indexes; re-running it over populated collections is safe — it `collMod`s rather than recreates). Verifying against a
real Mongo exposed two defects in `db/mongo/00-bootstrap.js`,
the same silent-failure class as the [[db-sql-scripts-drift-trap]]: **(1) database-name casing** — the script used
`cryptopaymentengine` while all three hosts + compose's `MONGO_INITDB_DATABASE` use `CryptoPaymentEngine`. MongoDB
forbids two DBs differing only by case, and the script runs FIRST (mounted into `docker-entrypoint-initdb.d`), so it
won the name and the app's first write would die with `db already exists with different case` — every Mongo-backed
feature silently broken in any fresh dev environment. **(2) validators written against an imagined schema** — the
`WalletResource` validator required camelCase `walletId`/`updatedAt` with `energy` as a BSON `long`, while the actual
`WalletResourceDocument` writes `_id`/PascalCase/base-unit strings, so **every Energy 5a resource write was rejected**
with `Document failed validation`; and `ResourceHistory` was declared under the unused name `WalletResourceHistory`, so
the app auto-created the real collection *without* the TTL index — append-only history growing forever. Fixed: name
aligned, `WalletResource` + `ResourceHistory` validators/indexes matched to the real documents, `Reconciliation` +
`ReconciliationHistory` added (they were absent entirely — `ReconciliationHistory` now has an index and, deliberately,
**no TTL**: the custody-drift trail is the one collection worth keeping indefinitely; `Drift` is signed so it takes
`^-?[0-9]{1,78}$`, not the unsigned `baseUnitString`), `EnergyDelegation` marked superseded (5b moved it to the SQL
`energy.EnergyOperation` aggregate), and `db/README.md` §2 gained a drift-check section naming the writers and flagging
that the eight remaining collections are declared ahead of use — nothing writes them, so their shapes are proposals,
not contracts. The corrected validators are proven **strict, not merely permissive**: with them live, an insert of the
old camelCase `WalletResource` shape is REJECTED, a numeric (non-string) amount is REJECTED (§14), an invalid `Status`
enum is REJECTED, and a negative `Drift` is ACCEPTED — i.e. they would have caught the original bug. **REQ-3's Mongo
path is verified end-to-end against the NATIVE MongoDB Windows service (8.2) with Docker fully shut down**
(previously only the degraded path was): seeded
snapshots matching the real document shapes pass the corrected validators, and `/ops/dashboard` returns
`custodyAvailable/energyHealthAvailable: true`, counts Drift(1) and Incomplete(1) **separately**, `energyWalletsCritical`
1 / `Low` 2, and the custody rows carry both the display decimal and the exact signed integer (USDC drift `-0.001` /
`driftBaseUnits: "-1000"` — the §14 point). `/ops/reconciliation` and `/ops/energy/resources` re-verified sorting
problems/worst-health first.

**Dev sample data (a demo portfolio for UI development) + two real environment defects fixed (2026-08-26) — BUILT,
verified end-to-end on a freshly dropped database.** An empty dev DB makes every UI screen look broken the same way,
so `Api/MerchantGateway` gained an opt-in `DevSampleDataSeeder` (`DevSampleData:Enabled`, default **false**;
testnet tier only, §10). **The design rule: it never writes money rows.** It seeds only *inputs* — merchants (via
`IMerchantRegistrar`/`IMerchantAssetPolicyService`), invoices (via `IPaymentIntentService`), **blocks on the
in-memory chain**, and withdrawal requests (via the same services the API and portal call) — and the REAL
scanner→confirmation→ledger→matcher→callback pipeline produces every deposit, journal, balance and callback.
Fabricating double-entry rows would bypass every ledger invariant (§14/§15) and drift from the code the moment
either changed; the only thing standing in for reality is the node, at the §8 DI seam the in-memory chain source
already occupies. Consequences (both intended): it requires `Chains:Tron:Live=false`, and it takes 30–60s because it
waits on the same workers a real deposit waits for (it polls `ILedgerQuery` before requesting withdrawals rather than
assuming). Idempotent (skips if `DEMOACME` exists). Seeds 3 merchants — `DEMOACME` (T+0), `DEMOGLOBE` (T+1, 50%
cash-out cap), `DEMOFROST` (**Frozen**) — each with 6 invoices (one unpaid, one **underpaid** so `amountMatched=false`),
5 credited deposits, and 4 withdrawals spanning `Confirmed`/`PendingApproval`/`PendingMerchantApproval`/`AwaitingFunds`.
**Ordering subtlety:** the settlement period + freeze are applied **last**, after the withdrawals — applying them first
is equally correct but leaves two of three merchants with an empty payout list (a poor demo); every gate stays live
either way (a new payout for `DEMOGLOBE` is still refused `exceeds_settled_balance`). Portal logins per tenant via a new
`DevMerchantPortalSeedOptions.AdditionalLogins` list (so portal work can verify tenant isolation, which a single-tenant
dev env cannot show). **Verified on a dropped+re-migrated DB:** all 12 withdrawals landed in the intended statuses, the
parked ones auto-resumed, and the ledger proves out — `SUM(Debit)-SUM(Credit) = 0`, TreasuryAsset 14,836.50 =
MerchantLiability 6,299.6825 + WithdrawalClearing 8,358.60 + FeeRevenue 178.2175 (a real non-zero fee split).
**Four real defects found while doing it.** (0) **`DateTimeOffset` was never reaching Mongo as a BSON date** —
the driver serialises a bare `DateTimeOffset` as a nested `{DateTime,Ticks,Offset}` **document**, so the
`bsonType:"date"` validators added on 2026-08-26 rejected **every** Energy `WalletResource`/`ResourceHistory` and
**every** Reconciliation snapshot write with a bare `Document failed validation` (the module logged
"Resource monitor skipped …" and carried on — a feature silently not recording, exactly the class the Mongo drift
rules warn about). Worse than the validator: a **TTL index on a non-date field expires nothing**, so
`ResourceHistory`'s TTL was inert and it would have grown forever. Fixed with
`[BsonRepresentation(BsonType.DateTime)]` on both documents' `ObservedAt` (UTC observations, no offset lost);
`db/README.md` §2 gained this as a third standing rule. Derived observability only — no money, no migration (§2).
(0b) **The demo seeder's staged energy readings silently did nothing, twice:** first because they targeted merchant
settlement addresses while the monitor only polls **platform** wallets from `IPlatformWalletDirectory`; then because
`InMemoryAccountResourceReader` is registered **only as `IAccountResourceReader`**, so
`GetService<InMemoryAccountResourceReader>()` returned null and the step no-op'd (the in-memory *balance* reader IS
registered concretely — the two differ, which is what made this easy to get wrong). Now resolved through the port and
type-checked. (1) **`tools/dev/Setup-LocalEnv.ps1` applied 10 of 15 DbContexts** — Sweep,
Treasury, Notification, MerchantIdentity and Audit were missing, so a fresh environment booted onto a schema the code
could not use (symptom: `Invalid column name 'SettlementDelayDays'`, far from the cause — the same silent-failure class
as [[db-sql-scripts-drift-trap]]). Fixed, plus a drift-check comment naming the `grep` that proves the list complete,
and `DOTNET_ROLL_FORWARD` defaulted in-script. (2) The identity/audit contexts **cannot use MerchantGateway as the EF
startup project** (it deliberately does not reference them, §4.7), so each context now names its own host. (3) **The
three hosts were on different databases**: the committed `appsettings.Development.json` defaults to LocalDB while the
setup script migrates the Docker SQL Server, and only MerchantGateway had a git-ignored `appsettings.Local.json`
override — so the Ops host showed **only `DEVMERCHANT`** and every dashboard tile read 0. **The symptom is an empty
screen, not an error**, which sends you debugging the API instead of the connection string. The script now writes the
override for any host missing it and **warns rather than overwrites** one that exists (it may hold a TronGrid key).
Docs: new `docs/dev-sample-data.md`.

**Frontend integration docs brought current (2026-08-26).** New **`docs/merchant-portal-frontend-integration.md`** —
the merchant-portal API (`Api/MerchantPortalApi`) had **no integration doc at all**, which is what `apps/merchant` was
actually blocked on (the UI team's REQ-4 was delivered long ago and they did not know). Covers the tenant-isolation
rule (the merchant id comes only from the session, never a request param), cookie+CSRF auth, the permission catalog,
the two-balance funds model (available vs settled/T+N), the two-party payout approval incl. reading
`awaitingPlatformApproval`, and the honest gaps (2FA unimplemented and the login `otp` **ignored**; **no `errorCode`
on this host** unlike Ops — branch on HTTP status). Also fixed real drift in
**`docs/backoffice-frontend-integration.md`**: **13 endpoints existed but were undocumented** (all 5 merchant-terms
setters, `/ops/reconciliation`, `/ops/sweeps`, both `/ops/energy/*`, all 4 `/ops/treasury/*`) — now written up as new
§19–§22, and §23's "known gaps" list rewritten (it still claimed Treasury/Energy/Sweep/Reconciliation screens and
detail endpoints did not exist, and that per-merchant limits were unenforced — all built since). Verified by script
that all 49 Ops routes now appear in the doc.

**Off-system merchant settlement + hot-wallet top-up recording (2026-08-28) — BUILT, full suite green, HTTP-verified.**
A change of direction the user specified: **merchant cash-outs are no longer paid by this system**. An admin audits the
request, a finance admin pays the merchant from a **company wallet OUTSIDE platform custody**, and the transaction is
recorded here after on-chain verification. Separately, when the hot pool runs low an admin tops it up from a company
wallet and records that too. **User payouts are completely unchanged** — still built/signed/broadcast automatically by
the KMS pipeline; the allocator, per-wallet lease and `AwaitingFunds` hold all remain, and now apply to user payouts only.
**Ledger impact (the part that had to be right):** two new **string** `AccountType`s ⇒ **NO ledger migration**.
`WithdrawalWalletTopUp` (credit-normal, System-owned) takes `Dr TreasuryAsset / Cr WithdrawalWalletTopUp` — custody
genuinely rises, so reconciliation stays exact, while the credit never touches a merchant account so
`merchant withdrawable = deposits − fees − settlements − payouts` holds **by construction**. `ExternalSettlement` takes
`Dr WithdrawalClearing / Cr ExternalSettlement` on a finance-settled cash-out — `TreasuryAsset` is deliberately NOT
credited, because no watched address was debited; crediting it (as an on-platform payout correctly does) would drift
reconciliation downward by every settlement ever made. The event carries a new backward-compatible
`WithdrawalConfirmed.ExternallySettled` flag, so the Ledger stays chain-agnostic and simply honours it.
**States:** `PendingAdminAudit → PendingFinanceTransfer → FinanceSettled`, + `Rejected` from either (releases the
reserve — a decline never strands merchant funds). `FinanceSettled` is deliberately distinct from `Confirmed`: different
origins of trust (the platform paid it vs a human asserted it and we verified). **Verification is the point:** new
keyless read-only `ITransactionVerifier` (Blockchain.Contracts) + `TronTransactionVerifier` over
`gettransactioninfobyid`, reusing the scanner's already-tested `TryMapTransfer` so verification and detection cannot
disagree. "Confirmed" means **solidified** (irreversible), not merely mined. Seven distinct failure codes so an operator
can tell "still confirming" from "wrong hash" from "wrong amount". `from` is recorded but NOT constrained (the admin
pays from whatever company wallet suits them). **Schema:** one migration `AddManualSettlementAndTopUp` —
audit/settlement columns + `withdrawal.HotWalletTopUp` + **two unique tx-hash indexes** (proven at the DB to reject a
duplicate: one real payment can never discharge two obligations). **Reconciliation** gained a custody breakdown
(`ColdTreasuryTotal`/`HotPoolTotal`/`DepositAddressTotal`/`ToppedUpTotal`) grouped from the SAME balance reads, so the
parts always sum to the whole; `TreasuryAsset` stays ONE ledger account. **Ops:** `audit-approve`/`audit-reject`
(`ops.withdrawals.approve`) · `record-settlement` (`ops.withdrawals.manage`, so signing off and declaring paid can be
different people) · `POST /ops/treasury/top-up` + `hot-pool` extended with live balances (unreadable ⇒ **null not 0**) ·
`GET /ops/settlement-activity` (one feed of company funds in/out, with ledger-derived running totals). **Earnings stay
`FeeRevenue` only** — `ExternalSettlement` is money going out and is reported as funds-deployed, never as income.
**Two corrections made during the build, both worth keeping:** (1) the §4.5 boundary refused
`ILedgerPoster` from Withdrawal.Application, so the top-up posting moved onto the **event/outbox path** — which also
removed a real durability gap (a crash between "recorded" and "posted" would have understated custody with nothing to
retry from); (2) a planned `PendingSettlementTotal` "explains drift" field was **dropped** — an external settlement
never moves either side of the equation, so it explains nothing, and an auto-"ExplainedDrift" status was dropped too
because the system genuinely cannot distinguish an unrecorded top-up from unexplained funds. Docs: `db/mongo/00-bootstrap.js`
validators updated **and re-applied + proven** alongside the document (the [[db-sql-scripts-drift-trap]] class);
`db/sql/70-withdrawal.sql` regenerated; `docs/backoffice-frontend-integration.md` §19b/§20/§20b/§21 written.

**`Platform/Compliance` — address screening, Phase 1 (2026-09-10) — BUILT, 17 tests green, host-boot verified.**
The third-party risk-score seam [[wallet-rotation-health-design]] deferred. Screens an address against an AML
provider and records what was decided and why. **Phase 1 touches NO money path** — the module, the vendor
adapter, the evidence trail and the tests exist; wiring it into withdrawals is Phase 2 (T3, design first).
**Its own module, not Blockchain** — a vendor's risk opinion is neither chain state nor a fact, and §8 forbids
business logic there; not Withdrawal either, since settlement wallets and (later) deposit senders need the same
answer and would otherwise cross a module boundary (§4.5). Schema `compliance`, migration `InitialCompliance`,
`db/sql/150-compliance.sql`. **Ledger impact NONE** (reads a public address, stores an opinion, holds no key §10).
**Two ports on purpose:** outward `IAddressScreeningService` (Contracts) is what Withdrawal/Merchant consume;
inward `IAddressRiskProvider` (Application) is the vendor seam — `MistTrackAddressRiskProvider` (real) ↔
`InMemoryAddressRiskProvider` (dev/test), chosen by DI exactly as the chain adapter is. Policy stays on OUR side:
the provider returns a score + its own band name, but the Allow/Review/Block thresholds are ours, so a vendor swap
cannot silently move our risk appetite. **Four outcomes, and `Unavailable` is deliberately its own** — folding it
into Allow drops the control during a vendor outage, folding it into Block hands a third party the power to halt
payouts; each caller must decide what an unknown means. **Sanctions indicators block regardless of score**
(`AlwaysBlockIndicators`, checked first) — a designation is a legal fact, not a gradient, so no threshold tweak
can let one through. **Evidence is append-only** (like the ledger): re-screening inserts a new row, and each row
keeps the raw provider JSON, our decision, AND `PolicyDescription` (the thresholds then in force) — storing only
the score would let a later threshold change rewrite history. A completed result is cached to `FreshUntil`
(`CacheDays`, default 30); a FAILED one never is, else one outage pins an address to "unknown" for a month.
**The binding constraint is the rate limit, not cost** — on the flat $689/mo Standard plan (10,000 calls/day,
**1 call/sec**) marginal cost is zero until quota runs out, so threshold-gating and a cheap-labels-first pass were
both dropped as pointless; the adapter paces itself, and that limiter is in-process, correct ONLY while screening
is drained under a single-flight worker lock (concurrent instances ⇒ must become a Redis token bucket).
`Compliance:Enabled` defaults **false** in code (screening is switched on deliberately, never off silently);
Production always takes the real adapter (a fake would fabricate clean scores indistinguishable from real ones,
§10); `MistTrack:BaseUrl` alone selects sandbox↔live so the SAME adapter code runs in both. Tests: policy +
cache (in-memory provider) and response mapping against payloads copied verbatim from MistTrack's sandbox docs
(fixtures, not live calls — a live test would spend daily quota and flake when the vendor is down). Smoke test:
`tools/dev/Test-AddressScreening.ps1` (SINCE REWRITTEN — the sandbox host turned out to reject a production key, so
its modes are now `-Mode Entitlement` / `-Mode Quota` / `-Mode Sandbox`; see the 2026-09-11 milestone below). Full write-up: `docs/address-screening.md`. **Deferred:** Phase 2 (gate the payout
path — queue + worker + park on Review/Unavailable, reusing the funding-hold states; T3), Phase 3 (inbound deposit
senders — needs `DetectedTransfer` to carry a From address, and an arrived deposit can only be flagged, never
refused), an ops surface (list flagged addresses + override a false positive — Phase 2 needs this on day one), and
per-merchant policy (thresholds are global config today).

**Address screening Phase 2 — the user-payout gate (2026-09-11) — BUILT, full suite green, HTTP-verified.**
Wires [[address-screening-vendor]]'s Phase-1 module into the money path: a USER payout's destination is
screened before anything is signed. **Scope is user payouts only** — a merchant cash-out pays to the
staff-whitelisted settlement wallet and already stops at `PendingAdminAudit` for a human, so screening
belongs where that wallet is whitelisted, not on every cash-out. **New status `PendingScreening`** (string-
stored, fits the existing `nvarchar(24)`) sits **after the merchant's sign-off and before the platform's**:
a payout the merchant will decline never spends a provider call, and staff reviewing one always have its
verdict in front of them, while a blocked payout never reaches staff at all. Entered from
`ConfirmReserved` (API payout) or `MerchantApprove` (portal payout), both gated on
`Withdrawal:Screening:Enabled`. **Outcomes:** Allow → threshold **re-resolved at that moment** (not carried
from request time, matching the merchant-approval path) → `Approved`/`PendingApproval`; Review or
Unavailable → `PendingApproval` **reusing the existing staff queue** rather than a second review state, so
the existing approve/reject IS the override (the row records WHICH of the two, since "risky" and "we could
not tell" call for different judgement); Block → `Rejected` **releasing the reserve** through the same event
path a staff rejection uses (a refusal must never strand merchant funds in clearing — asserted on balances).
**Worker, not request path:** the provider allows ~1 call/sec, so inline screening would serialise into the
API request and time out, and a timed-out screening is a payout with NO verdict — the one outcome worth
avoiding. `WithdrawalScreeningWorker` drains the queue instead; its **single-flight lock is load-bearing**,
because the adapter's rate limiter is in-process and two instances would each pace correctly yet breach the
limit together (remove that lock ⇒ the limiter must become a Redis token bucket). The worker is registered
**unconditionally** — gating registration on config would strand any payout already queued when the flag was
turned off, and those hold a live ledger reserve. **Config `Withdrawal:Screening:{Enabled, OnUnavailable}`
is deliberately SEPARATE from `Compliance:*`**: Compliance answers "how risky is this address", the payout
flow answers "what do I do about it", so a second consumer can answer differently without renegotiating a
shared policy. `OnUnavailable` defaults **Hold** (a vendor outage grows a staff queue, which a human can
clear) and never Allow (which trades an unscreened payout for continuity — the exact exposure screening
prevents). **Schema:** migration `AddWithdrawalScreening` — three nullable columns `ScreeningId`/
`ScreeningDecision`/`ScreeningScore`; `ScreeningId` is an opaque cross-module reference into
`compliance.AddressScreening`, deliberately NOT an FK (§4.5). The verdict is **snapshotted** onto the payout
rather than re-read, because the evidence is append-only and a later re-screen of the same address must never
appear to change what THIS payout was judged on. `70-withdrawal.sql` regenerated (BOM + QUOTED_IDENTIFIER
header re-added). **NO ledger impact** — screening decides whether a payout proceeds, never what is posted.
**Ops:** `WithdrawalAdminRow` gained `screeningDecision`/`screeningScore`/`screeningId` (staff see WHY on the
same screen, not in a lookup they might skip) and the effective-status vocabulary gained `pending_screening`
as **its own bucket**, not folded into `pending` — a queue waiting on a third party and one waiting on us
call for different responses. The existing drift-guard test caught the new status immediately, as designed.
Tests: 7 new pipeline tests on real SQL Server (clean-below-threshold sends; clean-above-threshold still
needs staff; block rejects AND releases; flagged holds with reason and reserve intact; unavailable holds by
default; unavailable passes only when configured; **screening-off never calls the provider** — the opt-in
regression guard). **HTTP-proven on a booted host** over the signed HMAC API: a clean destination cleared and
reached Broadcast; an unconfigured provider returned Unavailable and **held for review rather than allowing**
(the fail-safe, observed by accident before the host had `Compliance` config); screening off routed straight
to Approved having never called the provider. **Deferred:** settlement-wallet screening at whitelist time
(the cheapest high-value call — one per merchant per chain; the `ScreeningPurpose.SettlementWallet` seam
exists but the Ops setter does not call it), inbound deposit-sender screening (needs `DetectedTransfer` to
carry a From address; an arrived deposit can only be flagged, never refused), a dedicated ops screen (flagged
payouts surface on the approval queue, but there is no screened-address list or on-demand re-screen), and
per-merchant thresholds (global config today).

**Settlement-wallet screening + a pre-existing settlement-wallet defect fixed (2026-09-11) — BUILT, full suite
green, HTTP-verified.** The highest value-per-call use of the AML provider: ONE call per merchant per chain,
protecting the destination every one of that merchant's earnings is paid to. `MerchantRegistrar.SetSettlementWalletAsync`
now screens the address before whitelisting it. **Deliberately asymmetric with the payout gate, and the
asymmetry is the design:** (1) **synchronous, not queued** — whitelisting is a rare deliberate staff act with
no burst to pace, so there is no reason to make an operator wait on a worker; payouts need the queue only
because they arrive in bursts against the ~1 call/sec limit; (2) **only a Block refuses** — a payout runs
unattended so anything short of clean parks it, but this runs WITH a human exercising judgement who may hold
context the provider lacks, so Review/Unavailable are **accepted and surfaced as warnings** rather than
overriding them. A Block is the exception because a sanctions hit is a legal fact, not a risk appetite.
**Screened BEFORE mutating**, so a refused address leaves any existing whitelist untouched — losing a good
settlement wallet to a failed replacement would halt that merchant's cash-outs for a reason unrelated to the
wallet on file. **Return type changed** `Task<Result>` → `Task<Result<SettlementWalletResult>>` carrying the
decision + score + warnings; the Ops endpoint emits `screeningDecision`/`screeningScore` plus a `warnings`
array (empty for clean/unscreened, so the normal case stays silent — a UI must not read a 200 as silence).
`screeningDecision: null` means "not screened", deliberately distinct from `"Unavailable"` (asked, no answer).
**Third config section `Merchant:Screening:ScreenSettlementWallets`** (default false) alongside `Compliance:*`
and `Withdrawal:Screening:*` — same split throughout: Compliance says how risky, each consumer says what to do,
and they must be switchable independently precisely because they answer differently. **The provider is an
OPTIONAL dependency** — `MerchantRegistrar` resolves `IAddressScreeningService` via `GetService` (hand-built
registration, not convention), so a host that never whitelists a settlement wallet (the merchant portal, which
composes Merchant but not Compliance) is not forced to compose Compliance just to boot (§15.10); enabled-but-
not-composed **fails loudly** rather than silently skipping the check, since a wallet that looks screened but
isn't is worse than an error. Portal-boot verified. **Real pre-existing defect found and fixed:**
`MerchantRepository.GetByIdAsync` never `.Include`d `SettlementWallets`, so `Merchant.SetSettlementWallet`
could not see an existing wallet, treated a REPLACEMENT as a first insert, and died on the
`(MerchantId, Chain)` unique index with a `DbUpdateException` — a 500. **Replacing a merchant's settlement
wallet had therefore never worked**; it went unnoticed because the dev seeder sets it once on a fresh DB and
nothing exercised the update path. Found by exercising the endpoint over HTTP, not by the type system. Fixed +
regression-tested (set, then replace, assert one row with the new address). Tests: 6 new in
`MerchantPersistenceTests` (block refuses AND persists nothing; review accepted with warning; unavailable
accepted with warning; clean is silent; **screening-off never calls the provider** — proven by a stub that
throws if reached, so passing is evidence rather than absence of it; and the replacement regression).
**HTTP-proven** on a booted Ops host: whitelisting returned `screeningDecision: "Allow"` with empty warnings,
the replacement path worked (it 500'd before the fix), and the evidence row recorded
`Purpose=SettlementWallet`. Docs: `docs/address-screening.md` §9. **Deferred:** periodic re-screening of
wallets already on file (a cached verdict expires, but nothing re-checks a stored wallet — an address clean
when whitelisted can be designated later), a dedicated ops screen, and per-merchant thresholds.

**Address screening — live entitlement confirmed, a false-positive defect fixed, and the ops screen built
(2026-09-11) — BUILT, 885 tests green (1 known pre-existing Merchant failure), HTTP-verified against the
REAL vendor API.** Three things, in the order they mattered.
**(1) The blocking question is answered: `/v3/risk_score` IS included in the Standard plan** — a live call
returned HTTP 200 with a fully scored response, so nothing in [[address-screening-vendor]] needs revisiting
on entitlement grounds. Two side findings: the **sandbox host returns HTTP 400 with an empty body for this
key** while the live host succeeds (so the smoke script's sandbox mode is unusable and a dev host holding a
key but the committed sandbox base URL reads `Unavailable` for everything — use the in-memory provider
locally, or point at live and accept the quota); and `tools/dev/*.ps1` **needed a UTF-8 BOM** — Windows
PowerShell 5.1 decodes a BOM-less file as ANSI, turning each em dash into a smart quote it accepts as a
STRING DELIMITER, so the script failed to parse. Same silent-encoding class as the db/sql header rule.
**(2) A real policy inversion, found only because the live response was read.** `AlwaysBlockIndicators`
(checked first, independent of score) was matched against the adapter's full indicator list, which folded in
**every** `risk_detail[].risk_type` — including INDIRECT exposure. Live proof: a widely used TRX address the
vendor scores **3/100, "Low"** carries `risk_type: sanctioned_entity` at `exposure_type: "indirect"`,
`hop_num: 3`, 2.7% of volume, through htx. Our policy would have **Blocked** it. Indirect sanctions exposure
is near-universal for any address with exchange history, so screening as written would have refused a large
share of legitimate payout destinations — the opposite of the design intent, since "a designation is a legal
fact, not a gradient" describes a DIRECT hit only. Fixed structurally: `AddressRiskReport` gained
**`Designations`** (the `exposure_type: direct` subset; a MISSING exposure type counts as direct, so an
unfamiliar shape over-refers rather than under-detects) alongside `Indicators` (everything, kept as evidence
and shown to reviewers); `Decide()` matches **Designations only**. Indirect exposure is not discarded — the
vendor already prices it into the score the thresholds judge, so a row may legitimately read `Allow` while
listing `sanctioned_entity`. Also found: **.NET config array binding APPENDS to the code default**, so
`AlwaysBlockIndicators` was duplicated in every stored policy string; de-duplicated at the point of use, and
the additive behaviour kept deliberately (a sanctions rule should not be deletable by editing a settings
file) and documented. Evidence rows now stamp `always_block_direct_only=…`. Tests: the live payload is a
verbatim fixture asserting indirect ⇒ evidence-only, direct ⇒ designation, missing-type ⇒ direct, plus a
policy-level test that indirect exposure at a low score Allows and is still recorded.
**(3) The ops screen** (the day-one gap Phase 2 named). `GET /api/v1/ops/compliance/screenings` (paged,
newest first, filters chain/decision/purpose/address/date, unknown value ⇒ 400 with a specific code),
`GET .../{id}` (adds the provider's verbatim payload — omitted from the list so fifty rows do not drag fifty
JSON blobs to render a table that shows none of them), and `POST .../re-screen` (ignores the cache, appends
a row). **No migration** — the table already carried the decision+screened-at index, added for exactly this
read. New Compliance Contract `IAddressScreeningDirectory` + `AddressScreeningDirectory` (§4.5, registered
in core since it reads stored rows and never contacts a vendor). New `IAddressScreeningService.ReScreenAsync`
— deliberately a separate method from `ScreenAsync`, because a per-payout cache bypass is exactly what the
~1 call/sec limit cannot absorb, so forcing a fresh call stays a human act; the three money-path test stubs
now THROW on it, making that a guard rather than a convention. Two new permission codes,
**`ops.compliance.view` and `ops.compliance.manage`** split precisely because re-screening SPENDS QUOTA and
a read-only analyst must not be able to exhaust the budget the payout queue depends on. **The counters count
distinct ADDRESSES at their latest verdict, not rows** — an address re-screened weekly would otherwise
dominate a "blocked" count and make it look like a workload; the list stays full history, so a row can appear
under an `Unavailable` filter while counting as `Allow` (verified live). An unreachable provider is **200
with `Unavailable`**, never a 5xx — the request worked, the answer is "we could not tell". **HTTP-proven on
a booted Ops host**, including a live re-screen returning `Allow` score 3 with `sanctioned_entity` in its
reasons: the designation split working end-to-end on real vendor data, on the exact response that would have
been Blocked before. **Quota accounting:** there is NO way to read consumption from the API — no rate-limit or quota headers, and
no usage endpoint (`v1/quota`, `v1/usage`, `v1/user_info`, `v1/account`, `v1/api_quota`, `v1/remaining_quota`,
`v1/balance` all answer `PageNotFound`), so the dashboard is the only source. The smoke script was rewritten
around that: `-Mode Entitlement` / `-Mode Quota` (one metered call with an explicit before-and-after, turning
the drop into a screenings-per-day figure) / `-Mode Sandbox` (now a diagnosis, since the sandbox rejects a
production key). Capacity is documented against the measured weight: at 1 unit/call the plan gives 10,000
screenings/day, at 10 units 1,000 — and a ~170/day workload (5,000 payouts/month, ignoring the 30-day cache)
fits with 6x headroom even on the worst assumption, so the weight matters for planning, not for whether to
enable screening. **The measured number is still unrecorded** — `docs/address-screening.md` §5 has the slot.
**Frontend linkage:** `SettlementWalletResult` gained `ScreeningId` (the Ops settlement-wallet response now
emits `screeningId`), because the withdrawal row already carried one and without it an operator seeing a
warning had no route to the indicators behind it — both screens now deep-link to
`GET /ops/compliance/screenings/{id}`, so one place renders the evidence and the two cannot disagree.
Docs: `docs/address-screening.md` §3/§5/§7/§10, `docs/backoffice-frontend-integration.md` §18 (vocabulary now
carries `pending_screening` + the screening enums), §22b, §24. **Still deferred:** whether the daily quota is flat or weighted per endpoint (needs a dashboard reading
taken around a known call count); periodic re-screening of wallets on file (staff can now force one, nothing
does it on a schedule); a hop/percentage threshold for indirect exposure (visible on the evidence, tune once
there are real hit-rate numbers); Phase 3 inbound deposit senders; per-merchant thresholds.

**Settlement-wallet re-screening + a process-wide rate limiter + the sibling of an already-fixed defect
(2026-09-11) — BUILT, 904 tests green (1 known pre-existing Merchant failure), both hosts boot-verified.**
Three things.
**(1) The rate limiter was per-instance, and the provider is TRANSIENT.** `AddHttpClient<TClient,TImpl>`
registers the implementation as transient, so the pacing gate held as a field on `MistTrackAddressRiskProvider`
was recreated on every DI scope with `_nextAllowedCall` back at `MinValue`. Pacing therefore held INSIDE one
worker pass and imposed no constraint between passes, between a worker and an HTTP request, or between two
workers in one process — each caller pacing itself perfectly while the process breached the plan limit by the
number of concurrent callers. Found while adding the second screening caller below, which would have doubled
the real rate. Not cosmetic: a breach answers 429, a 429 is a screening with NO verdict, and the payout gate
holds a no-verdict payout for staff, so an unpaced burst converts straight into a queue of held payouts. Fixed
by extracting **`MistTrackRateLimiter`** as a SINGLETON injected into the (still transient) provider. Tests
assert two scopes share one gate, that the provider is transient (documenting WHY the gate cannot live on it),
that a real composition resolves MistTrack and not the fake (§10), and that a zero rate does not divide by zero.
Still only correct within one process — the single-flight lock on every screening path is what makes an
in-process limiter sufficient; remove it and this must become a Redis token bucket.
**(2) Periodic settlement-wallet re-screening** (the deferred item from the settlement-screening milestone).
Whitelisting screens an address ONCE; a verdict is a snapshot, so an address clean on approval day can be
designated months later while every one of that merchant's earnings keeps being paid to it. New
`SettlementWalletScreeningService` + `SettlementWalletScreeningWorker` in **`Merchant/Workers`** — the
module's first file in that layer since the 2026-08-13 prune (§4.3: create a layer when its first real file
lands). **It FLAGS ONLY and never revokes**, which is the design: revoking would let a vendor opinion (or its
outage) halt a merchant's earnings with nobody in the loop, and a human is already in the loop because every
cash-out stops at `PendingAdminAudit`; a revocation is also destructive and needs re-approval to undo, while a
flag costs nothing either way. **`Unavailable` is deliberately NOT on the worsening scale** (Allow<Review<Block)
— a provider outage is not news about the address, and alarming on it is how a real alert gets ignored; a
first-ever verdict is flagged only if itself bad. **It costs almost nothing to run often**: the pass goes
through `ScreenAsync`, so a still-fresh verdict is served from cache — `Compliance:CacheDays` drives quota, not
the interval, and the pass reports how many wallets actually cost a call. New Contract
`IMerchantSettlementDirectory.ListAllAsync` (unpaged on purpose: one row per merchant per chain, and the caller
screens all of them anyway, so paging would add a cursor and a skipped-wallet bug class for nothing). Config
`Merchant:Screening:{RescreenSettlementWallets (default OFF), RescreenIntervalHours (12)}` — kept SEPARATE from
`ScreenSettlementWallets` because whitelisting is a handful of calls a month while this is one per merchant per
chain per cycle forever. Worker registered unconditionally and gated inside, so enabling it is a config change
not a redeploy. Provider resolved SOFTLY like the registrar, so the portal host still boots; enabled-but-not-
composed logs an error rather than silently checking nothing. **Surface:** `MerchantSettlementWalletView` gained
`ScreeningDecision`/`ScreeningScore`/`ScreeningId`/`ScreenedAt`, read from STORED evidence only (no provider
call, so opening a merchant spends no quota and a vendor outage cannot break it) and only on the per-merchant
read — `GetPagedAsync` does not even load settlement wallets, so annotating the list would add a per-row cost
to a screen that shows none of it. `screenedAt` matters as much as the decision: an old verdict on a high-value
destination is itself worth seeing. HTTP-verified returning `Allow`/score 0/id/timestamp.
**(3) The sibling of the settlement-wallet loading defect, found by reading the boot log.**
`MerchantRepository.GetByIdAsync` was fixed when replacing a wallet was found to 500; **`GetByCodeAsync` had
the identical omission and was missed**, so `DevMerchantSeeder` — which resolves by code then calls
`SetSettlementWallet` — failed `IX_MerchantSettlementWallet_MerchantId_Chain` on EVERY boot of an already-
seeded database. The rule, now written beside the code since it has been missed twice: **a read that hands
back the aggregate to be MUTATED must load the whole aggregate**; partial loading is only safe for a read-only
projection, and those go through `MerchantDirectory`. Fixed + regression-tested at the seeder's exact shape
(load by code, replace, assert one row); boot-verified from one such failure per boot to zero. Docs:
`docs/address-screening.md` §5/§11, `docs/backoffice-frontend-integration.md` §22b.

**Inbound screening (Phase 3) — watching OUR OWN deposit addresses, + a config-binding bug that doubled the
bill (2026-09-11) — BUILT, 915 tests green (1 known pre-existing Merchant failure), HTTP-verified.** The user
set the shape: an inbound transfer cannot be checked while it is moving, so the best available control is to
check our own deposit wallets, manually and on a schedule. That is exactly right, and it is why sender
screening was never the design — there is nothing to screen until the transfer lands, and once it lands it
CANNOT be refused (an arrived deposit is credited, and a frozen merchant's deposits still credit the ledger,
§14). The available signal is the other side of the same graph: a provider scores an address from its
history, so tainted inflow raises the score of OUR receiving address.
**It RECORDS AND FLAGS ONLY** — no deposit reversed, no credit withheld, no wallet disabled. The restraint is
firmer than the settlement pass because by flag time the money has already reached a merchant's balance, so
any automatic reaction would mean clawing funds back on a vendor's say-so.
New `ScreeningPurpose.DepositAddress` (string-stored ⇒ NO migration), deliberately distinct from
`DepositSource` (a counterparty). New `DepositAddressScreeningService` + `DepositAddressScreeningWorker` in
**`AssetManagement/Wallet/{Application,Workers}`** — Wallet owns deposit addresses, so it owns this, by the
same logic that put the settlement pass in Merchant; Workers is that module's first file in the layer since
the 2026-08-13 prune. **Scheduled and manual are separate on purpose:** the manual sweep
(`POST /ops/compliance/deposit-addresses/screen`, `ops.compliance.manage`) bypasses the ENABLED switch so
staff can check on demand without a standing spend, but NOT the per-pass cap — a manual run costs what a
scheduled one costs. Composition mirrors that split: `AddDepositAddressScreening` registers the service in
both hosts, the worker ONLY in the money host (§4.7 — the ops host runs no background work).
**The per-pass cap is the load-bearing control.** Deposit addresses are the one candidate set that grows
without bound, and this shares a quota with the payout gate — the control that actually holds money — so an
uncapped sweep could spend a day's budget and leave payouts unscreenable. `MaxAddressesPerPass` (100) bounds
a pass BEFORE it starts; the remainder is picked up next pass, and `candidates` vs `screened` (plus a log
line) says plainly when the cap is biting. Only FUNDED addresses are candidates (an unused address has no
graph). Candidate selection is ONE indexed query, not one round trip per address: new Contract
`IAddressScreeningService.FindAddressesNeedingScreeningAsync` returns the subset lacking a fresh verdict,
capped at the budget (and returns nothing at all when screening is disabled, rather than sending a caller off
to write an Unavailable row per address). Config `Wallet:Screening:{Enabled (default OFF), MaxAddressesPerPass,
IntervalHours, Chains}`.
**The bug this found, live and costing real quota: .NET binds a configuration array by ADDING to the code
default.** `"Chains": ["Tron"]` against a default of `[Chain.Tron]` bound to `[Tron, Tron]`, so the sweep
looped twice over the same chain and screened every address TWICE — observed as `candidates: 2` for a single
address, then `candidates: 1` after the fix. The same binding behaviour was found earlier on
`AlwaysBlockIndicators`, where it was merely noisy; here it doubled the bill. Both are now de-duplicated at
the point of use, and an audit confirms they are **the only two array-typed options in the codebase with a
non-empty default** — any array option added later behaves the same way. Regression-tested with the exact
binder output. Docs: `docs/address-screening.md` §12, `docs/backoffice-frontend-integration.md` §22b + the
status vocabulary. **Still not built:** `DepositSource` (screening the actual sender) stays unimplemented and
unimplementable as a gate — `DetectedTransfer` carries no From address, and even with one an arrived deposit
could only be flagged, which is what this milestone already achieves from the other direction.

**Proximity rule for indirect exposure (2026-09-11) — BUILT, OFF BY DEFAULT, 926 tests green (1 known
pre-existing Merchant failure), verified against the LIVE vendor API.** The knob the decision model was
missing, shipped disabled so the thresholds become a config change rather than a deployment once there is
real data to set them from.
**The gap it closes:** since the designation split, ALL indirect exposure is treated identically — recorded
as evidence, left entirely to the vendor's score. That is the safe default and it is what makes screening
usable, but it treats 60% of volume one hop from a sanctioned entity the same as 0.1% five hops away. Those
are different facts.
**The rule:** an INDIRECT finding whose risk type is in `AlwaysBlockIndicators`, within
`Compliance:IndirectReviewMaxHops` AND at or above `Compliance:IndirectReviewMinPercent`, is raised to
**Review — never Block**. Block stays reserved for a direct designation, a legal fact rather than a matter of
degree; proximity is a gradient, so the most it justifies is a person looking. **Both conditions must hold**:
distance alone would flag nearly every address with exchange history (the failure the rule exists to avoid,
not to cause), and weight alone would flag an address whose whole history traces to something bad fifteen
removes away. **It can only RAISE a clean result** — score thresholds are evaluated first, so a direct
designation still blocks and a high score still blocks. It reuses `AlwaysBlockIndicators` rather than taking a
second list, so the two cannot drift; a drifted compliance rule is worse than a blunt one.
**Required a provider-contract change:** flat indicator strings can only answer "is it designated", so
`AddressRiskReport` gained **`Exposures`** (`RiskExposure`: risk type, direct/indirect, hops, percent,
entity) alongside `Indicators`/`Designations`. `MaxHops = 0` disables the rule and is the default, so
shipping this changes nothing anywhere until someone configures it. Thresholds in force are stamped onto
every evidence row (`indirect_review<=3hops>=1pct`) so a decision stays explainable after the setting changes.
**Why disabled, and why the numbers are NOT guessed:** set loosely, every payout queues for staff, which
trains people to approve without looking and makes the control worse than nothing; set tightly, it never
fires and nothing changed. Every screening stores the full provider payload, so the evidence to choose them
is already accumulating — measure the hop/percent distribution of real destinations once volume has run
through. Reference point from live data: an ordinary TRX address the vendor scores 3/100 reads
`sanctioned_entity` at **2.735% of volume, 3 hops** out through an exchange; any threshold catching that will
catch most legitimate destinations.
**Verified on the LIVE API:** the same address, same unchanged score of 3, screens `Allow` with the rule off
and `Review` with it set to 3 hops / 1% — so the rule and not the score moved it; the evidence row carries
the thresholds that produced the decision. Tests: 10 (off-by-default, close+heavy ⇒ Review, the real live
reading still passes a sensible threshold, close-but-negligible ignored, heavy-but-distant ignored, a risk
type outside the designation list unaffected, a direct designation still Blocks with the rule on, thresholds
recorded on the evidence) plus 2 adapter tests that hops/percent/entity survive the mapping intact. Docs:
`docs/address-screening.md` §3 + §13. **The screening deferred list is now down to one item: per-merchant
policy** (thresholds are global config), which waits on the same real hit-rate numbers.

**Screening thresholds made back-office configurable (2026-09-11) — BUILT, 940 tests green (1 known
pre-existing Merchant failure), HTTP-verified end to end.** The hop limit and every other tuning knob can now
be set from the UI, which is what the measure-then-set advice needed to be actionable.
**The split, and it is the security decision:** the TUNING knobs (block/review scores, cache days, hop limit,
volume floor, added designations) are API-editable; the MASTER SWITCHES (`Compliance:Enabled`,
`Withdrawal:Screening:Enabled`, `Merchant:Screening:*`, `Wallet:Screening:Enabled`) stay in configuration.
Thresholds decide how a control is calibrated; switches decide whether it runs at all. A stolen admin session
must not be able to silently switch off the gate that holds money — keeping that in config means it takes
infrastructure access, not a browser tab. `GET .../policy` NAMES those keys and the reason, so a settings
screen says why the switch is absent instead of leaving an operator hunting for it.
**Config is the floor, a saved version is the override.** Config alone ⇒ every tuning change is a deployment;
DB alone ⇒ a fresh environment boots with NO policy on a control that decides whether money moves, which is
the worst possible default. `source` (`Configuration`/`Stored`) distinguishes "nobody has set this" from
"someone set it to exactly the default" — otherwise indistinguishable, and only one is a question worth
asking; `configuredDefaults` stays visible beside `current`.
**Append-only and attributed**, like the evidence it governs: new `compliance.ScreeningPolicyVersion`
(migration `AddScreeningPolicy`, `150-compliance.sql` regenerated with BOM + QUOTED_IDENTIFIER header). A
payout allowed last month must stay explainable against the thresholds actually in force, which a mutable
settings row destroys. `updatedBy` comes from the validated session via the existing `AuditActor` helper and
NEVER from the body — an attribution the caller supplied is not an attribution. A change also writes a
warning-level log line. **The designation list is ADD-ONLY**: staff may add, but cannot remove what the
platform ships with, because a sanctions override is exactly the rule that should not come off in a web form;
`editableIndicators` is the removable subset.
**Cross-host propagation:** the ops host serves the API, the money host runs the workers. New
`ScreeningPolicyProvider` (scoped) + `ScreeningPolicyCache` (SINGLETON — a per-scope cache would expire every
request and cache nothing) resolves the effective policy with a **30-second** window, so a change reaches the
workers without a restart; the saving host invalidates its own cache immediately. `AddressScreeningService`
now resolves thresholds through the provider ONCE per screening (so every rule in one call is judged against
one consistent set) and reads only `Enabled` straight from config.
**Validation lives in the domain** (`ScreeningPolicyVersion.Create`), not at the edge, since these are the
rules themselves: scores 1-100, review at or below block (a higher review floor means nothing EVER reaches
review — silently useless is worse than refused), cache 1-365 days (zero re-screens on sight and exhausts the
quota), hops 0-10 (zero disables the proximity rule; beyond ten a finding describes the network, so more only
LOOKS cautious), percent 0-100, attribution required. New `ComplianceErrors` with stable dotted codes. A
refused update persists nothing.
**HTTP-proven on a booted Ops host:** read `Configuration` before anything saved; PUT hop limit 2 / 5% with an
added designation returned `Stored` attributed to the session user with the shipped designations intact and
only the added one editable; all six validation rules returned their codes at 400; history showed the version
with its note; and a subsequent screening stamped `indirect_review<=2hops>=5.00pct` onto its evidence row —
the saved policy reaching the DECISION PATH, not just the settings screen. Docs:
`docs/address-screening.md` §14, `docs/backoffice-frontend-integration.md` §22b (all 67 ops routes verified
documented). **Deliberately NOT exposed:** the master switches above, and the three consumer modules'
operational settings (`RescreenIntervalHours`, `MaxAddressesPerPass`, `OnUnavailable`) — those are deployment
characteristics rather than risk appetite, and can be promoted the same way if wanted.

**Ops API verified end-to-end for front-end use, 4 defects found and a host-wide envelope guarantee added
(2026-09-11) — BUILT, 946 tests green (1 known pre-existing Merchant failure).** Asked to confirm the front
end can actually use every endpoint, so all **75 route-and-method pairs** were EXERCISED over HTTP against a
booted host rather than checked off against source — path params filled with a valid-shaped but nonexistent
id and bodies sent empty, so writes answered with validation or not-found instead of mutating anything.
**Four defects found that way, each of which would have hit the UI:** `POST /ops/accounts` and `POST
/ops/roles` returned **500** on a null username/name (the value reached `Trim()`; the validation errors
`staff_user.username_required` and `role.name_required` already existed and were simply never reached) —
guarded in the SERVICES, not the endpoints, since that is the boundary every caller crosses;
`POST /ops/treasury/reload/{id}/submit` returned 500 because `Convert.FromHexString(null)` throws
`ArgumentNullException`, which the `FormatException` catch never saw; and `GET /ops/treasury/hot-pool`
returned **400 with NO ENVELOPE** because `chain` was a required minimal-API parameter, so a request without
it failed model binding before any code ran.
**That last one is systemic, so the fix is too.** New **`OpsExceptionMiddleware`**, registered FIRST so it
wraps CORS, auth, authorization and routing: a `BadHttpRequestException` (missing required query param,
malformed JSON body, wrong-shaped route value) becomes **400 `ops.malformed_request`** and any unhandled
exception becomes **500 `ops.internal_error`** — both in the standard envelope, with the detail logged and
NEVER returned. A client-aborted request is swallowed quietly rather than answered on a closed connection,
and a response that has already started writing is left alone (a truncated response beats a malformed one).
There was no global handler at all before this, so REQ-7's `errorCode` contract silently did not hold for the
two classes no endpoint ever saw.
**Browser behaviour proven, not assumed:** credentialed CORS preflight from the Vite origin returns the
origin + `allow-credentials: true` and permits `X-CSRF-Token`, an unlisted origin gets no `allow-origin`
back; login sets the httpOnly `cpe_ops_session` cookie and returns a `csrfToken`; the cookie alone
authenticates a read; a cookie write WITHOUT the CSRF header is 403 `ops.csrf_invalid` and WITH it reaches
the handler; logout revokes and the next request is 401.
**Permission gating proven with a genuinely restricted user** — an admin's wildcard passes every gate, so a
role holding only `ops.merchants.view` was created and logged in: 200 on `/ops/merchants`, 403
`ops.permission_denied` on `/ops/sweeps`, `/ops/compliance/policy` and `/ops/accounts`, and the denied WRITE
refused too rather than merely hidden. Tests: 6 new (both guards, null/empty/whitespace). Docs:
`docs/backoffice-frontend-integration.md` §2 (envelope guarantee + the two new codes) and new §23b recording
what was exercised, what it proved, and what it does NOT cover (write success paths stay service-tested, the
project's convention for Ops endpoints).

**Back-office integration guide rebuilt for a front-end build (2026-09-11).** The user is starting the admin
portal, so `docs/backoffice-frontend-integration.md` gained three things it lacked, all verified rather than
asserted. **§0 Start here:** how to boot the host, the dev login, the CORS allow-list trap (a credentialed
request needs an EXACT origin, so a Vite server on an unlisted port fails every call), the five conventions
that each cost an afternoon (check `isSuccess`; branch on `errorCode` never prose; cookie writes need
`X-CSRF-Token`; rates are percent on the wire; money is a display decimal PLUS an exact base-unit string),
and a ~40-line TypeScript client that gets all five right. **The client was RUN against a live host exactly
as printed** — login, authenticated read, `/auth/me` session restore, a CSRF-carrying write, a typed 404, and
the 401 redirect path all behaved as shown; `redirectToLogin` is now `declare`d, since it is called only on a
401 and an undefined function would break at session expiry, the worst moment to discover it. **§0a Every
route at a glance:** all 75 route/method pairs GENERATED from the endpoint source with permission and a
pointer to the section documenting the payload — every one mapped, and every permission string validated
against the real `OpsPermissions` constants. **§0b Suggested build order:** ten screens with their endpoints
and dependencies, login-shell first because nothing works until cookie + CSRF + permission-driven nav are
right. Claims spot-checked live rather than copied from older sections: login returns
`token/csrfToken/permissions` and `/auth/me` returns `permissions` + a fresh `csrfToken` (so it doubles as
session restore); a withdrawal row carries `expectedAmount` + `expectedAmountBaseUnits` + `decimals`; a fee
row exposes `depositFeePercent`/`withdrawalFeePercent` with no bps field. The preamble now says the doc was
exercised over HTTP, not merely read against source.

**REQ-26 — current-verdict list + batch latest lookup for the admin portal (2026-09-14) — BUILT, 956 tests
green (1 known pre-existing Merchant failure), HTTP-verified.** Filed by the admin-portal team in
`platform-admin-frontend/docs/backend-requirements.md`; every factual claim in it was checked against source
first and held. **The defect:** `GET /ops/compliance/screenings?decision=X` filters ROWS, so an address blocked
then cleared still matched `decision=Block` through its old row forever — fine for an audit trail, fatal for a
work queue. **Two new reads, both `ops.compliance.view`, stored evidence only (no quota):**
`GET /api/v1/ops/compliance/addresses` (one row per chain+address at its latest verdict; filters decision/
purpose/chain/stale/page) and `POST /api/v1/ops/compliance/screenings/latest` (latest verdict for up to 200
addresses on one chain). New Contract methods `IAddressScreeningDirectory.SearchCurrentAsync` /
`FindLatestForAddressesAsync` + `CurrentVerdictFilter`/`CurrentVerdictPage`/`LatestScreeningLookup`/
`ScreeningLookupLimits`. **No migration.** **Rules:** reduce first, filter second; `purpose` selects WHICH
addresses (ever screened for it) while the verdict stays the latest row whatever its purpose, so re-screening a
deposit address from the payout screen cannot drop it from the deposit queue; `stale` = `FreshUntil` null or
past; `totalCount` counts addresses; `summary` honours every filter EXCEPT `decision` (the counters describe
the population the table is drawn from). Batch: one entry per distinct address in request order echoed as
sent, exact duplicates collapse but case variants do not (TRON Base58 is case-sensitive), `null` means never
screened only, >200 ⇒ 400 `ops.too_many_addresses` (never truncated), a missing `addresses` field ⇒ 400
`ops.address_required` so a misspelt field cannot succeed about nothing. **One definition of "latest"
everywhere — `ScreenedAt` then `Seq`, deliberately NOT `Id` as the request asked:** SQL Server orders a
`uniqueidentifier` by its last six bytes first, so `ORDER BY Id` is deterministic but unrelated to write order
even for v7 GUIDs; `Seq` is the clustered identity = insertion order. The cache probe
(`AddressScreeningRepository.FindLatestAsync`, the read the payout gate acts on) previously ordered by
`ScreenedAt` alone and now uses the same rule, as do the counters. Reduction is a NOT EXISTS so filtering,
counting and paging compose in SQL on `IX_AddressScreening_Chain_Address_ScreenedAt`. Also: the decision/
purpose parsers now reject NUMERIC enum strings (`Enum.TryParse` accepts "7"), which previously parsed to a
nonexistent value and matched nothing. Tests: 10 SQL Server (incl. a timestamp tie resolving identically in
all four readers). HTTP: 21 checks incl. the 200/201 boundary and 403 for a restricted user. Docs:
`docs/address-screening.md` §15, `docs/backoffice-frontend-integration.md` §2/§22b/§23b + §0a regenerated (77
route/method pairs). **Frontend side not edited from here** — REQ-26's status and the per-row lookup
composable live in the frontend repo.

**Staging operations: the IIS worker outage, nightly-shutdown tooling, and a KMS custody switch for the testnet tier
(2026-09-14) — BUILT, KeyManagement 122 tests green, gateway round-trip verified on a real database.** A tester's Nile
deposit on staging never confirmed. Root cause was not code: **the gateway had not been running for five days.** It
is hosted in-process under IIS, which starts an app on its first request and stops it on idle or recycle; the
scanner, confirmation worker, outbox relay and expiry worker stop with it, and payers never call the gateway, so
nothing woke it. Fixed operationally (app pool `AlwaysRunning`, no idle timeout, no recycle, preload); for production,
run MerchantGateway as a Windows service. Five scripts in `tools/dev/` now cover the box, all ASCII+BOM for PowerShell
5.1, secrets shown only as fingerprints, state changes dry-run unless `-Force` (`docs/ec2-staging.md` §6):
`Get-HostConfigReport.ps1` (effective config per IIS site; found DGP-BOAPI2 carrying a stray gateway DLL and the
portal on the repo's dev pepper/signing key), `Get-DepositDiagnosis.ps1` (node → tx → cursor → deposit → outbox →
invoice → logs), `Set-ScanCursor.ps1`, `Start-StagingPlatform.ps1` (the nightly/weekend boot: services, IIS tuning,
scanner catch-up vs skip, verify; registers itself as a startup task), and `Switch-StagingCustody.ps1`. **Cursor
rule:** catching up never misses a payment and a weekend takes ~25 min, so the cursor is left alone unless the
catch-up exceeds an hour; a longer gap skips only to just before the earliest moment an invoice was payable (block
found by timestamp), and every skip is logged for a later rescan. **KMS on the testnet tier (T3, user-approved:
code change, retire-and-restore, testnet-only CMKs):** previously only Production could use KMS. New
`AddTestnetKeyCustody(config, reconcileWallets)` registers KMS custody when `KeyManagement:Kms:Enabled`, else the
in-memory store, never both (the §10 interlock is unchanged), and throws at boot if KMS is on without region + both
ARNs. All three hosts use it; only MerchantGateway passes `reconcileWallets`, and skips the staking seeder under KMS
(it imports a private key; there is no Energy CMK). **The switch itself is domain code, not SQL:** wallet rows record
their store and the filtered unique index allows one active wallet per (merchant, chain, purpose), so a switched-off
wallet would block its replacement. `CustodyModeReconciler` (KeyManagement.Application, run once at gateway boot by
`CustodyModeReconciliationService`, which stops the host on failure) archives the other store's active wallets, saves,
then reactivates the most recently archived wallet of the store in force per group, in one transaction. New
`HdWallet.Reactivate` (Archived only; Disabled stays disabled; derivation index untouched, so no address is reissued)
+ `IHdWalletRepository.ListForCustodySwitchAsync`. **No migration, no ledger impact.** Money effects, documented: deposits
keep crediting (detection is keyless); archived addresses cannot be swept or paid out from until switched back (sweep
fails pre-sign, payout parks `AwaitingFunds` with reserve held); KMS mode starts an empty KMS hot pool. Tests: 6
reconciler tests on SQL Server (archive frees the slot, round trip restores with indexes intact, idempotent, disabled
never restored, most-recent wins, domain guard) + 6 composition tests (exactly one store, missing identifier refuses,
switch ordered before the re-seeder). Verified by booting the real gateway build: KMS on archived 6 wallets, off
restored 6 with identical indexes, missing ARN refused to start with nothing changed. **Not verified:** sealing a seed
under a real CMK (no AWS credentials locally; `kms-go-live.md` step 3 on the box). **Known, deferred:** Sweep's scan
keeps creating sweeps for funded addresses whose key is archived, each failing pre-sign (noise, no funds move);
staging still uses the leaked TronGrid key and the portal the repo's dev pepper/signing key (user: rotate at prod).

**Merchant API IP allowlist enforced (2026-09-17) — BUILT, Merchant 188 tests green (1 known pre-existing failure),
HTTP-verified on a booted gateway.** Found while planning the Windows-service work: `AllowedIps` could be edited in both
portals (and synced to Cloudflare from Ops) but **was never checked on a merchant API call** — the port from the legacy
`APIGateway` kept its Cloudflare edge sync and dropped `MerchantSecurityFilter`'s per-merchant check, so anyone holding
a merchant's API key and signing secret could call from anywhere. The edge sync alone could not substitute: it admits
the UNION of every merchant's addresses. **User decisions:** trust `CF-Connecting-IP` only from Cloudflare's ranges;
exact addresses only (no CIDR); **an empty allowlist denies every call**; leave the Cloudflare sync as is.
**Where:** the Merchant module owns the rule. `MerchantIpAddress` (Domain) defines a valid entry — a single full
address, IPv4 as four decimal octets, normalised (IPv4-mapped IPv6 → IPv4, IPv6 canonical lowercase, zone id dropped),
refusing CIDR/ports/host names and the shorthand/octal forms `IPAddress.TryParse` silently accepts (`1.2` = `1.0.0.2`).
`Merchant.UpdateAllowedIps` normalises and refuses the WHOLE update on any bad entry; `MerchantConfiguration.AllowsIp`
compares normalised addresses, returns false for an empty list, a null caller, or a stored entry that no longer parses
(e.g. a CIDR range the portal used to accept). **Shared contract change:** `IMerchantRequestVerifier.VerifyAsync` gained a
REQUIRED `IPAddress? clientIp`, checked LAST — after the signature and `CanTransact` — so a wrong key/signature from any
address stays the uniform 401 `merchant.invalid_credentials` and only an authentic call from the wrong place gets
`merchant.ip_not_allowed` (published as `MerchantRequestVerificationErrors.IpNotAllowed` so the host branches without
touching Domain, §4.5). The verifier logs a warning with merchant code + the address seen. **Gateway:** new
`ClientIpResolver` believes `CF-Connecting-IP` only when the TCP peer is inside `Gateway:ClientIp:TrustedProxyRanges`
(Cloudflare's 22 published ranges in `appsettings.json`, fetched 2026-09-17; code default EMPTY because config arrays
append to code defaults), otherwise uses the peer; through a trusted proxy with a missing, repeated or unparseable header
the caller is unknown ⇒ refused, never attributed to the proxy. Built at boot so a malformed range stops the host. The
middleware maps the code to **403 "IP address not whitelisted."** (the legacy message). **Edit paths unified:** Ops and
portal both use `AllowedIpInput.Partition` (Application) — Ops keeps its contract (saves valid, reports `invalidIps`,
400 only if all refused), the portal refuses the whole list on any bad entry and **no longer accepts CIDR**; both reads
and writes return `apiAccessBlocked`. **Dev seed:** `Merchant:DevSeed:AllowedIps` (`127.0.0.1`, `::1` in the gateway and
portal Development configs), applied only while the list is empty so a restart never undoes an edit. **No migration,
no ledger impact.** Tests: 29 domain allowlist tests (accept/refuse tables, mapped IPv4, IPv6 forms, empty/null deny,
stored CIDR matches nothing, whole-update refusal, partition), 4 verifier tests (unlisted ⇒ 403 code, empty list, unknown
caller, bad signature from an unlisted address still 401), a seeder test (applied once, never overwrites), and 12
`ClientIpResolverTests` in `Api.IntegrationTests` (forged header from outside Cloudflare ignored,
IPv6 edges, mapped peers, repeated/listed/garbage headers ⇒ unknown, bad range refuses). **HTTP-verified** on a booted
gateway with signed requests: listed 200; unlisted 403; forged `CF-Connecting-IP` naming the listed address from localhost
403; bad signature from an unlisted address 401; empty list 403. **Rollout (breaking by design):** every non-closed merchant
with a NULL or CIDR-containing list is refused on deploy — pre-deploy query and staging steps in `docs/ec2-staging.md` §7.
**Deferred:** moving the Cloudflare sync into the Merchant module so portal edits sync too; revisiting client-IP trust if
a reverse proxy (IIS ARR) is put in front of the gateway on the box — loopback must NOT simply be added to the trusted ranges.

**Treasury cold reload REMOVED + Ops top-up verifier fixed (2026-09-17) — Treasury 9 tests green, migration applied
twice to a clean database, Ops host HTTP-verified against Nile.** The in-system cold reload (Phase 2 above:
`TreasuryReload` aggregate, build-unsigned → client-side sign → gateway broadcast/confirm) was superseded. Product owner
confirmed it obsolete on 2026-09-07 (recorded in both frontend repos' `backend-requirements.md`), and the user
confirmed the real flow: **deposit addresses → sweep → cold treasury; hot withdrawal pool → the merchant user's
destination; a low hot wallet is funded by finance from a company wallet OUTSIDE the system and recorded as a top-up.**
Cold funds never move to the hot pool in-system. **Removed:** `TreasuryReload`, its enums and errors, `TreasuryReloadService`
+ `ITreasuryReloadService`, `TreasuryReloadProcessingService`, repository + EF map, the whole `Treasury/Workers` project
(its only file was the reload worker; removed from the solution and the gateway), `POST /ops/treasury/reload` and
`/reload/{id}/submit` + request models, the two `/dev/treasury/reload*` routes, both reload test files, and the Ops
host's reload-only transaction builder. **Kept:** the cold treasury wallet (Sweep's destination, counted by
Reconciliation) with `POST /ops/treasury/cold-wallet`, the hot pool, and top-up recording. The two errors the cold-wallet
code borrowed from the reload moved to `TreasuryColdWalletErrors`; `AddressRequired`'s code changed
`treasury.reload.address_required` → `treasury.cold_wallet.address_required` (no frontend used it). **Schema (T3,
user-approved):** migration `RemoveTreasuryReload` drops `treasury.TreasuryReload`; `db/sql/130-treasury.sql`
regenerated with BOM + header and proven by applying it twice to a fresh LocalDB database. Pre-deploy in-flight check
and archive query: `docs/ec2-staging.md` §8. **No ledger impact** (the reload never posted). **Real defect fixed on the
way:** top-up recording needs `ITransactionVerifier`, which the Ops host registered ONLY in its Development block (via the
in-memory chain), so `POST /ops/treasury/top-up` could not work under Staging or Production — and under Development it
verified against the fake chain, refusing every real Nile hash. New `AddTronTransactionVerifier(config)` (Blockchain
Infrastructure: read-only TRON RPC client + `TronTransactionVerifier`, keyless) registered by Ops outside Development or
when `Chains:Tron:Live=true`, BEFORE the Development block so the in-memory `TryAdd` cannot shadow it. HTTP-verified on
the new Ops build (Development + Live against Nile): a never-seen hash → real `gettransactioninfobyid` call → 400
`verification.tx_not_found`, nothing recorded; both reload routes 404; cold-wallet registration still answers.
**Noted, not built:** the user described top-ups as two steps (admin creates the record in the UI → finance transfers
on-chain → the record is updated with the hash), while today a top-up is recorded in one step after the transfer.

**Cold COLLECTION wallets + taint segregation + admin-owned sweep dials + multi settlement wallets (2026-09-18) —
BUILT, 1022 tests green (1 known pre-existing Merchant failure), db/sql applied twice to a clean database.** Four
connected changes, all T3 (schema + where money goes), agreed with the user before building.

**(1) Cold collection wallets: several per chain, one active per kind.** `TreasuryColdWallet` gained `Kind`
(`Safe`/`Danger`), `Status` (`Active`/`Retired`), `Label` and a screening snapshot; the unique index moved from
`(Chain)` to a filtered `(Chain, Kind) WHERE Status='Active'` plus a unique `(Chain, Address)` (migration
`ColdCollectionWallets` — its `DEFAULT 'Safe'`/`'Active'` backfill is load-bearing: EF's generated empty strings
would have left the running platform with no active destination and stopped sweeps silently). **Re-pointing is now
add-and-activate, never an edit of an address in place** — the old address usually still holds funds, and rewriting
it would drop those funds from the custody total and read as a shortfall. Retired rows are kept forever and
Reconciliation now sums **every** cold address on a chain (`ListCustodyAddressesAsync`), both kinds, active and
retired. Two real gaps closed on the way: the registration path had **no address-format validation at all** (the
same `IAddressEncoderFactory.IsValidAddress` a payout destination gets — a typo here became the destination of every
future sweep), and **no screening**, while a merchant settlement wallet had both.

**(2) Taint segregation — the deposit address's verdict picks the destination.** `SweepScanService` screens a
deposit address before creating the sweep: `Allow` → the Safe collection wallet, `Review`/`Block` → the Danger
(quarantine) wallet, no fresh verdict → `Sweep:Screening:OnUnavailable` (`Hold` by default). **A flagged balance is
never swept to the Safe wallet as a fallback** — with no Danger wallet registered, or no provider composed, the
sweep is simply not created and the balance stays on a deposit address the platform also controls. Un-mixing is
impossible after the fact, which is why the decision is made before the transfer rather than sorted out afterwards;
`Review` quarantines alongside `Block` because segregating a clean balance is reversible by a human and mixing a
tainted one is not. Cost is bounded twice: only addresses already over the sweep threshold are screened at all, and
`MaxScreeningsPerPass` (25) caps live calls, the rest served from stored evidence via one indexed
`FindAddressesNeedingScreeningAsync`. Each sweep snapshots `DestinationKind`/`ScreeningId`/`ScreeningDecision`
(migration `SweepSettingsAndDestinationKind`), so a later re-screen never appears to change what a past sweep was
judged on. New `ScreeningPurpose.ColdCollectionWallet` (string-stored ⇒ no Compliance migration); the collection
wallets' own verdicts are recorded and **never refuse anything**, because the quarantine wallet is expected to
score badly — treating that as a disqualification would disable the control exactly when it works.

**(3) Sweep dials owned by the back office.** New `sweep.SweepSettings` (one row per chain): enabled, threshold,
confirmations, scan interval, plus the schedule state (last run, sweeps created, manual-request marker). Config is
the floor — a row is created from `Sweep:Policies:{chain}` and keeps tracking it until staff save, after which
`source` reads `Stored`. One scoped `SweepSettingsService` serves both the workers (`ISweepPolicyProvider`, now
`ForAsync`) and the API (`ISweepSettingsService`), so a settings screen cannot disagree with what the scan uses.
The scan worker now ticks every 30s and asks whether a pass is **due** rather than being the schedule itself.
**The manual trigger is a request, not a scan**: the ops host runs no sweep workers and holds no chain credentials
(§4.7), so `POST /ops/sweeps/scan/{chain}` stamps the row and the money host claims it on its next tick, consuming
it exactly once across instances. New permission `ops.sweep.manage`, deliberately split from `.view` — the
threshold decides when customer funds move and pausing stops them moving at all. `Enabled` lives in the row rather
than config, unlike a screening master switch: pausing is the *cautious* direction, so it is reachable from the
back office.

**(4) Merchant settlement wallets: several on file, one active.** Same shape as the collection wallets —
`Status`/`Label` + a filtered unique `(MerchantId, Chain) WHERE Status='Active'` and a unique
`(MerchantId, Chain, Address)` (migration `MerchantSettlementWalletSet`, `Status DEFAULT 'Active'` so every existing
row stays the destination). `IMerchantSettlementDirectory` resolves the **active** one, and the re-screen pass
covers active wallets only. **Screening no longer refuses the whitelisting** (the user's call): a directly
designated address is saved with its verdict and simply cannot be made active
(`Merchant:Screening:BlockActivationOnScreeningBlock`, default true) — the money-path control is kept while the
record of the decision survives, instead of forcing an operator to re-enter an address the platform already judged.
Activation re-screens, because that is the moment the address starts receiving earnings. `PUT .../settlement-wallet`
keeps its old contract (add + activate).

**A real defect found by exercising it on a booted host, not by the type system.** Switching a merchant's
active settlement wallet returned **500** — intermittently. Only one row per (merchant, chain) may be Active, and
EF chooses its own UPDATE order, so retiring the incumbent and activating the replacement in one `SaveChanges`
was rejected by the filtered unique index whenever the activate happened to go first. The first swap in the same
session succeeded and the next failed, which is the worst shape a bug can take. Fixed the way the Treasury path
was already written: retire and **save**, then activate and save, inside one transaction
(`IMerchantRepository.InTransactionAsync`) — two separate transactions would be worse still, since a crash
between them leaves the merchant with no destination and every cash-out refused. The dev seeder does the same
two-phase save (`Merchant.PrepareSettlementWallet` + `ActivateSettlementWallet`, the convenience one-save setter
deleted rather than left as a trap). Regression test swaps back and forth six times on real SQL Server, because a
single swap can pass on ordering luck; HTTP-verified afterwards with six consecutive 200s on the exact call that
had 500'd.

**Ops surface:** `GET/POST /ops/treasury/cold-wallets` (+ `/{id}/activate`, `/retire`, `/re-screen`; the singular
`POST /ops/treasury/cold-wallet` still answers), cold rows carrying live balances (unreadable ⇒ **null, not 0**);
`GET /ops/sweeps/settings` (also at the original `/policies`), `PUT /ops/sweeps/settings/{chain}`,
`POST /ops/sweeps/scan/{chain}`; `POST /ops/merchants/{id}/settlement-wallets` (+ activate/retire). Sweep rows now
emit `destinationKind`/`screeningDecision`/`screeningId`. **No ledger impact anywhere in this work** — a sweep
relocates funds between addresses the platform already controls, whichever collection wallet receives them.
Tests: 8 cold-wallet registration tests, 8 sweep-routing tests (incl. "flagged is held rather than swept to Safe"
and the screening-off provider-never-called guard), a reconciliation test that quarantine and retired addresses
still count towards custody, and settlement-wallet multi/activate/retire persistence tests. Docs:
`docs/address-screening.md` §16, `docs/backoffice-frontend-integration.md` §0a/§19/§20/§22/§22b.
**Deferred:** nothing spends from the quarantine wallet (the key is not in the system, so any movement is a human
act) and there is no per-source quarantine report; a balance swept before its address went bad is not re-routed.


Every other module in the map is a placeholder in this doc, not yet on disk — scaffold a module
only when real feature work on it starts, creating only the layers it uses (§4.3).

**Persistence rules in force:** one `DbContext` + one SQL schema + one migrations-history table per
module; money is `decimal(38,0)` via `.UseBigIntegerMoney()`; PKs are `Guid.CreateVersion7()`;
append-heavy tables get a non-clustered GUID PK + clustered `bigint IDENTITY Seq`
(`HasSeqClusteredIndex()`). See `docs/database-design.md`.

### 4.3 Module structure (canonical layout — a module has only the layers it actually uses)
    <Module>/
      Api/            # module's own endpoints/minimal-API groups, mapped by the host
      Application/    # use-cases, CQRS handlers, validators
      Domain/         # entities, value objects, domain events — NO external deps
      Infrastructure/ # EF Core/Mongo/Redis/chain-SDK adapters implementing this module's ports
      Contracts/      # public DTOs/interfaces other modules or hosts may reference
      Events/         # integration events this module publishes/consumes
      Workers/        # BackgroundServices owned by this module
      Tests/

This is the *canonical* layout, not a mandate to create all eight. **Create a layer only when its first real file
lands** — the 2026-08-13 prune removed 18 empty layer-projects that were scaffolded but never filled (see §4.2). In
particular, `Api/` is usually **absent**: HTTP endpoints live in the hosts (`Api/MerchantGateway`, `Api/OperationsApi`),
not in per-module `Api/` projects. `Reconciliation` / `Notification` / `Treasury` are the reference shape — they carry
only the layers they use. Keep consistency in *naming and internal shape* of the layers a module does have; don't
pre-create empty ones.

A module must be understandable without reading any other module.

### 4.4 Dependency direction
    APIs → Gateway Host → Application → Domain → Infrastructure
Domain and Application reference **NO** infrastructure — no EF Core, no Redis, no
Mongo, no chain SDK. They talk out only through ports (interfaces they define,
Infrastructure implements). Infrastructure never references business modules.

### 4.5 No module-to-module dependencies (the most important rule)
A module must **never** call another module's Domain/Application/Infrastructure
directly (`await _withdrawalService.Process(...)` from inside Deposit is forbidden).
Modules communicate only through: Domain Events, Integration Events (§7.5), shared
Contracts, interfaces, or the SharedKernel. A module may reference another module's
`Contracts`/`Events` project — never anything else of another module.
**DB relationships:** default to reference-by-`Guid` across modules (no cross-module FK), so
modules stay independently extractable. A cross-module FK is allowed only as a *documented,
deliberate exception* where two things are genuinely inseparable — it welds them to one
physical database. Within a module, use FKs freely.

### 4.6 Single responsibility per module
Each module owns exactly one business capability (e.g., Ledger owns journal entries
and account balances and must not know TRON/Ethereum exist; Blockchain owns node
communication/RPC/broadcasting and must contain no business logic). See
`Scaffolding.md` for the full per-module responsibility list.

### 4.7 Gateway Host responsibilities
Each host under `Api/` (`MerchantGateway`, `OperationsApi`) is only the entry point:
DI composition, module registration, configuration, middleware, background worker
registration, startup. **No business logic lives in a host.**

### 4.8 SharedKernel — allowed vs. forbidden
Allowed: base Entity, Value Objects, Domain Event base, `Result<T>`, exceptions,
constants, cross-cutting interfaces, extensions.
Forbidden: any business logic (Deposit/Withdrawal/Wallet/Blockchain rules). If it
encodes a business rule, it belongs to a module, not SharedKernel.

## 5. How I Want You To Work (Technical Lead mode)
You are my Senior Architect / Lead Backend Engineer. Challenge weak decisions;
surface security, concurrency, scalability, and data-integrity risks early;
offer better alternatives with trade-offs. Don't just agree.

- Consequential decisions: give Decision · Reason · Alternatives · Trade-offs · Future impact.
- Small decisions: one line of rationale. Match depth to stakes.
- Ambiguous + risky ⇒ ask before building. Ambiguous + trivial ⇒ state assumption, proceed.

**Priority order** (never trade higher for lower without saying so):
Correctness → Security → Maintainability → Scalability → Readability → Performance → Speed.

## 6. Task Tiers (scale process to risk)
- **T1 Trivial** (rename, log, doc, isolated non-money bug): just do it; summarize after.
- **T2 Standard** (new endpoint, new chain adapter, refactor within a module):
  short design note, then implement. Approval only if it changes a shared contract
  or a module boundary.
- **T3 Sensitive — approval before code** (anything touching ledger/balance, money
  math, key handling/signing, withdrawal flow, idempotency, DB schema, chain
  confirmation/reorg logic, cross-module event contracts, or a new module boundary):
  full flow — requirements → design + trade-offs → schema → implement per layer,
  review between steps.

When unsure, treat money- or key-touching work as T3.

## 7. Boundary Policies
### 7.1 Result<T> → HTTP
- `Result<T>` for expected/business failures; exceptions only for the truly unexpected.
- Map `Result` → HTTP in ONE place (an endpoint filter / result mapper) per host, not
  per controller. Success → 2xx; business failure → RFC-9457 ProblemDetails with a
  stable error code.
- Inbound webhooks (chain-scan providers / PSPs) return the ack THEY expect, and
  must be verified (signature/HMAC) + idempotent before any side effect.

### 7.2 EF Core / SQL Server conventions
- Tables singular, columns PascalCase. `nvarchar` for Unicode text (names/descriptions),
  `varchar` for ASCII (addresses, hashes, hex). DateTimes native `datetimeoffset`, UTC.
  Optimistic concurrency via native `rowversion` (`.IsRowVersion()`), not an app-managed column.
- **Crypto amounts are integers in base units** (wei = 1e-18, sun = 1e-6, lamports
  = 1e-9), modeled as `System.Numerics.BigInteger` in Domain. **Store as `DECIMAL(38,0)`**
  — scale 0 (integers), NOT a scaled display value like `DECIMAL(38,18)`. Mapped by the custom
  `BigIntegerTypeMapping` (enable per DbContext with `.UseBigIntegerMoney()`); a plain
  `ValueConverter<BigInteger, decimal>` silently caps at ~28 digits and must never be used for
  money. 38 digits covers every real asset; >38-digit amounts are rejected at ingestion, never
  truncated (see `docs/database-design.md` §1.2). **Never `double`/`float`, ever.** Keep the
  display decimal (18/6/9) as `Asset` metadata, convert only at the edge.
- One `DbContext` + one SQL Server **schema** per module; each module owns its own migrations
  history table. No cross-module FKs (reference other modules by opaque `Guid`).
- PKs are `Guid` (`Guid.CreateVersion7()`). Index every FK and every idempotency-lookup column.
- Index every FK and every column used for idempotency lookups.

### 7.3 Idempotency (mandatory for every money operation)
- API writes require a client `Idempotency-Key`; persist it with a UNIQUE constraint
  and return the stored result on replay.
- On-chain deposits dedup on `(ChainId, TxHash, LogIndex/OutputIndex)` UNIQUE — the
  DB is the arbiter, not app logic.
- Ledger is append-only. Never edit history; post a compensating entry.

### 7.4 Concurrency
- Guard each account/address mutation with a **Redis distributed lock**
  (DistributedLock.Redis), keyed `(ChainId, Address)` or `(UserId)` — single-flight,
  TTL > worst-case op. Prefer stateless services; no mutable shared state without a lock.

### 7.5 Cross-module events (in-process today, Kafka-ready by contract)
Modules never know their transport. Two interfaces in `SharedKernel`, defined once,
never touched when the transport changes:
```csharp
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default);
}

public interface IIntegrationEventHandler<TEvent>
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
```
- **Today:** `IEventBus` → in-process implementation (handlers run in the same
  process, same transaction boundary as the Outbox write).
- **Outbox pattern from day one:** the publishing side writes the event to an Outbox
  table in the same DB transaction as the business change; a dispatcher relays it to
  `IEventBus`. This is what makes the ledger-affecting event flow durable and
  idempotent, not just a convenience.
- **Tomorrow:** `IEventBus` → Kafka producer. Same event contracts, handlers move to
  separate services with minimal change. Do not add Kafka before it's needed —
  design contracts as if they will be, don't build the broker early.
- Event contracts (integration events) live in each module's `Events/` project so
  other modules can depend on the shape without depending on the implementation.
- One handler failing must never break unrelated handlers.

## 8. Blockchain abstraction (capability segregation)
Do NOT model one fat `IBlockchainProvider`. Model **capabilities** as small ports
in `Gateway.Core/Blockchain/Providers` (Application layer of the Blockchain module);
each chain adapter in `Gateway.Core/Blockchain/Providers/Infrastructure` implements
only the subset it supports; resolve per chain via `IBlockchainProviderFactory.For(chainId)`.
    IAddressDeriver · IBalanceReader · IFeeEstimator · ITransactionBuilder ·
    ITransactionBroadcaster · IDepositWatcher · IConfirmationTracker
    (+ optional: IMemoResolver for tag chains, IUtxoSource for BTC-like)
TRON/ETH/SOL are all account-model, so this is safe today. Write down that a
UTXO chain (BTC) will need `IUtxoSource` + different building logic — it is NOT free.

**`IAddressDeriver` is split, deliberately.** Key *derivation* (BIP-32/SLIP-10) lives in
**KeyManagement**, because it touches key material and must never pass through the chain-integration
module (§10). Address *encoding* from a public key (`IAddressEncoder`) lives in **Blockchain**, since
a public key is public data. Derivation is client-side maths — no node RPC can do it, and the RPC
methods that touch keys (`personal_sign`, TRON `/wallet/gettransactionsign`) require sending the
private key over the wire. Never use them.

**ed25519 ≠ secp256k1.** Solana (SLIP-0010) supports *hardened derivation only*, so its addresses
cannot be derived from an xpub; Tron/Ethereum (BIP-32) can. Model this as `DerivationScheme`, and
let unsupported capabilities be *absent* rather than throwing.

The Blockchain module is pure infrastructure/integration — it must contain **no
business logic**. It proves a transaction occurred; it never decides ownership.
The Ledger module is the only source of truth for who owns what.

`Scanner` and `Synchronization` (chain polling / reorg detection) publish domain
events (e.g. `DepositDetected`, `ChainReorgDetected`); they never call another
module directly.

## 9. Background Workers
`BackgroundService`, owned per-module (`<Module>/Workers/`), registered by the
Gateway Host at startup. Every job idempotent, retryable, resumable via a persisted
cursor. Per-chain deposit watcher (Blockchain/Scanner) → confirmation tracker
(Blockchain/Synchronization) → publish `DepositConfirmed` → Ledger module credits.
Handle **reorgs**: only credit after N confirmations; if a confirmed tx is
orphaned, post a compensating ledger entry. Broadcaster retries with backoff,
never double-sends.

## 10. Security (crypto-critical)
- Private keys/seeds live in an HSM/KMS, never in DB, code, config, or logs.
  Signing is isolated behind a signer port; the app handles unsigned/signed blobs only.
- Hot/cold wallet separation; withdrawals above a threshold require approval.
- Verify every webhook signature; prevent replay (nonce/timestamp + dedup).
- **Never log:** private keys, seeds, mnemonics, JWT/API secrets, passwords,
  connection strings, full PII.

## 11. Preferred Libraries
- Central Package Management: **`Directory.Packages.props`** at the repo root — every
  project references versions from there, no per-project `Version=`.
- Data: **EF Core 10** + **Microsoft.EntityFrameworkCore.SqlServer** (10.0.9, first-party —
  always in lockstep with EF Core, so no provider-version risk).
  **StackExchange.Redis**; **MongoDB.Driver**; **DistributedLock.Redis** (Medallion).
- Result: **ErrorOr** (or a hand-rolled `Result<T>`) — pick ONE, use everywhere.
- Validation: **FluentValidation** (Apache-2.0, free).
- Mapping: **Mapperly** (source-gen). **Do NOT use AutoMapper or MediatR (now commercial).**
  For CQRS dispatch use **Mediator** (martinothamar) or a hand-rolled sender.
- Resilience: **Microsoft.Extensions.Http.Resilience** for outbound RPC/PSP calls.
- Chain SDKs (Blockchain module Infrastructure ONLY): Nethereum (ETH), Solnet (SOL), TronNet/HTTP (TRON).
- Testing: **xUnit v3**, **NSubstitute**, **Shouldly** (or AwesomeAssertions —
  NOT FluentAssertions ≥8, commercial), `Microsoft.AspNetCore.Mvc.Testing`.
  **Integration tests are local-by-default, overridable by environment — nothing spins up its own
  container.** SQL Server: `(localdb)\MSSQLLocalDB`, override `CPE_TEST_SQL`. MongoDB:
  `mongodb://localhost:27017`, override `CPE_TEST_MONGO` (own database `cpe_test_energy`, dropped either
  side of a run). Unreachable ⇒ **skip, not fail**, and fail fast (~3s) rather than stalling on a driver
  default. **Testcontainers was removed** (2026-08-26): it needs a Docker daemon, so the two Mongo store
  tests were the only ones in the solution that silently skipped for a developer running Mongo natively —
  and a test that skips on the machine of the person changing the code is close to no test at all.
- **No message broker (Kafka) is wired yet.** §7.5 defines the contract so it can be
  added later without touching module code — do not add the dependency early.

The libraries listed in this document are the preferred defaults for this project.

Use them unless there is a strong technical reason not to.

If recommending an alternative library:

- Explain why the current choice is insufficient.
- Describe the benefits and trade-offs.
- Consider migration cost and long-term maintenance.
- Wait for approval before introducing a new core dependency.

Consistency across the codebase is preferred over introducing newer libraries with only marginal benefits.

## 12. Design Defaults
SOLID · DRY · KISS · YAGNI · composition over inheritance · DI everywhere.
Prefer consistency with existing patterns over novelty; justify any new pattern.
Money math, idempotency, reorg handling, and every chain adapter require tests.
No business logic in SharedKernel, Infrastructure, or a Gateway Host — see §4.

## 13. Architectural Principles

When implementing a feature, always design in the following order:

1. Business Requirements
2. Domain Model
3. Ledger Impact (if applicable)
4. Persistence Model
5. Background Processing
6. Public API
7. Frontend Integration

Never design from the controller downward.

Always design from the business domain outward.

The domain model should drive the database schema, APIs, and implementation—not the other way around.

If a feature affects money movement, explicitly describe how it impacts the ledger before implementing any code.

---
## 14. Money Rules

Financial correctness is more important than implementation convenience.

Always follow these rules:

- Never use `double` or `float` for financial calculations.
- Never perform money calculations using display values.
- Always perform calculations using blockchain base units (`BigInteger`).
- Convert to human-readable decimal values only at the API or UI boundary.
- Never round or truncate values unless explicitly required by the blockchain protocol or business rules.
- Never modify historical ledger entries. Corrections must be represented by compensating ledger entries.
- Every financial operation must be idempotent and fully traceable.
- Every balance must be derivable from the immutable ledger.
- Preserve precision throughout the entire processing pipeline.
- Prefer correctness and auditability over performance optimizations.

If a proposed implementation could compromise financial correctness, stop and explain the risk before continuing.

---
## 15. Non-Negotiable Rules (Modular Monolith)

1. No direct module-to-module dependencies.
2. No business logic in SharedKernel.
3. No business logic in Infrastructure.
4. Ledger is the only financial source of truth.
5. Blockchain is only the external state.
6. Modules communicate only through contracts, events, or interfaces.
7. Every module owns its own domain.
8. Every feature must preserve module boundaries.
9. Never bypass the architecture for convenience.
10. Design every module as if it will become its own microservice in the future.
