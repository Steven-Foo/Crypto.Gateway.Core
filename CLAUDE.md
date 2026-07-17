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
        MerchantGateway/   # merchant-facing host — composition root
        OperationsApi/     # internal/ops host — composition root
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
**Follow-ups (none block the spine):** `/transactions/query`, withdrawal callbacks (needs a per-tx callback URL
on Withdrawal), a dedicated callback delivery worker (backoff/abandon vs. the current outbox-retry), and
Development auto-migration (the host doesn't migrate on boot — dev DB is migrated manually per `docs/dev-round-trip.md`).

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

Every other module in the map is a placeholder in this doc, not yet on disk — scaffold a module
only when real feature work on it starts, following the same 8-layer layout.

**Persistence rules in force:** one `DbContext` + one SQL schema + one migrations-history table per
module; money is `decimal(38,0)` via `.UseBigIntegerMoney()`; PKs are `Guid.CreateVersion7()`;
append-heavy tables get a non-clustered GUID PK + clustered `bigint IDENTITY Seq`
(`HasSeqClusteredIndex()`). See `docs/database-design.md`.

### 4.3 Module structure (identical for every module — consistency mandatory)
    <Module>/
      Api/            # module's own endpoints/minimal-API groups, mapped by the host
      Application/    # use-cases, CQRS handlers, validators
      Domain/         # entities, value objects, domain events — NO external deps
      Infrastructure/ # EF Core/Mongo/Redis/chain-SDK adapters implementing this module's ports
      Contracts/      # public DTOs/interfaces other modules or hosts may reference
      Events/         # integration events this module publishes/consumes
      Workers/        # BackgroundServices owned by this module
      Tests/

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
  NOT FluentAssertions ≥8, commercial), **Testcontainers** (SQL Server/Redis/Mongo),
  `Microsoft.AspNetCore.Mvc.Testing`.
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
