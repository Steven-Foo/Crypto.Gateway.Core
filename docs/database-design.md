# Crypto Payment Engine — Database Design (Reconciled Spec, SQL Server)

**Status:** DRAFT for approval (T3). No EF Core code exists yet — this document is the
contract we implement against. It reconciles `DBDesign.md` and
`WAllet KMS Mnemonic Design.md` with `CLAUDE.md` and the modular-monolith scaffold.

**System of record: Microsoft SQL Server** (EF Core 10, `Microsoft.EntityFrameworkCore.SqlServer`).
MongoDB holds blockchain/external state only.

Where the source docs conflict or violate a `CLAUDE.md` non-negotiable, **this spec wins** and
the change is marked **[FIX]**. Items already approved by the owner are marked **[APPROVED]**.
Items still needing a decision are in §15.

---

## 1. Global Conventions

### 1.1 Identifiers
- **Every PK is a `Guid` generated with `Guid.CreateVersion7()`** (time-ordered), stored in a
  native **`UNIQUEIDENTIFIER`** column.
- **Clustering (SQL-Server-specific, [§15.6]):** SQL Server sorts `UNIQUEIDENTIFIER` by a
  non-byte order that does **not** match v7 time order, so a *clustered* GUID PK fragments on
  insert. Default pattern for **high-write** tables (JournalEntry, Deposit, Outbox,
  SigningAudit, WalletAssignment, MerchantWebhook): **PK is non-clustered on the GUID; a
  `bigint IDENTITY` column `Seq` carries the clustered index** (monotonic inserts, no
  fragmentation). Low-volume config/reference tables (Merchant, HDWallet, Asset, Account,
  Configuration, …) keep a clustered GUID PK for simplicity. **[APPROVED §15.6]** — applied
  per-table by actual write pattern, not blanket.
- Cross-module references are **plain `Guid` values, never DB foreign keys** (§1.6).

### 1.2 Money — the single most important rule  *([APPROVED])*
- All monetary values are **integer base units** modeled as `System.Numerics.BigInteger`.
- **Stored as `DECIMAL(38,0)`** — a real numeric column at **scale 0** (integers), holding the
  raw base unit (sun / lamports / wei), **not** a scaled display value.
  - **Scale is 0, not 18.** Base units are already integers — they have no fractional part. The
    per-asset `6 / 9 / 18` decimal is *display metadata* (`Asset.Decimals`, §4) applied only at
    the API edge, never a storage scale. Storing a scaled decimal (e.g. `DECIMAL(38,18)`) would
    mean persisting display values and converting in/out on every read/write — exactly what §14
    forbids, and it risks rounding on any non-clean division.
  - **Range:** 38 integer digits covers every real asset with margin (ETH wei ≈ 27 digits; a
    33-digit Shiba-scale supply still fits). An amount exceeding 38 digits is **rejected loudly**
    at ingestion (`^[0-9]{1,38}$`), never truncated (§14).
  - **Mapping mechanism (verified against SQL Server 2025, not assumed):** a custom
    `BigIntegerTypeMapping : RelationalTypeMapping` registered via `IRelationalTypeMappingSourcePlugin`
    (`UseBigIntegerMoney()`), reading/writing through `SqlDecimal` at the ADO layer.
    Two simpler routes were tried and **rejected on evidence**:
    - `ValueConverter<BigInteger, decimal>` — `System.Decimal` overflows past ~28 digits
      (`decimal.MaxValue` = 7.92e28). Stores ETH wei fine, **throws** on a 34-digit token amount.
      This would have silently capped the system below the advertised 38 digits.
    - `ValueConverter<BigInteger, SqlDecimal>` — EF rejects it outright: *"the database provider
      does not support mapping 'SqlDecimal' properties to 'decimal(38,0)' columns."*
    Round-trip is asserted exact at 1/18/27/28/29/34/38 digits by
    `BigIntegerMoneyMappingTests`; those tests are the guard against an EF upgrade regressing this.
  - Stored as an **unsigned magnitude** (base units are non-negative); direction is expressed by
    which column holds it (Ledger `Debit` vs `Credit`), never a `-`.
- **Balances:** maintained **incrementally in the same transaction** as each ledger posting
  (BigInteger math in the app). A SQL `SUM` over `JournalEntry` is available for reconciliation on
  bounded sets, but is **not** the primary path: SQL Server does *not* promote precision, so
  `SUM` over `decimal(38,0)` raises *"Arithmetic overflow error converting expression to data type
  numeric"* once the running total exceeds 38 digits (verified). App-side `BigInteger` summation
  has no such cliff.
- **Never** `double`/`float`. **Never** store a display/decimal-scaled value.

### 1.3 Time
- Native **`datetimeoffset`**, always UTC. `CreatedAt` on every table; `UpdatedAt` only on
  mutable tables; append-only/immutable tables omit it.

### 1.4 Concurrency
- Mutable tables carry a native **`rowversion`** column mapped with EF `.IsRowVersion()` —
  DB-maintained, no app bookkeeping. **[Improvement vs MySQL]** (MySQL had no native rowversion;
  SQL Server does). Append-only tables (JournalEntry, SigningAudit, Outbox, AuditLog, all Mongo
  logs) omit it — they are never updated in place.

### 1.5 Deletion
- **Financial and custody records are never physically or soft-deleted.** Corrections are
  compensating ledger entries or status transitions. Soft delete (`IsDeleted`/`DeletedAt`) is
  used only on genuinely mutable non-financial config where history matters, called out
  per-table. Default: no delete.

### 1.6 Modular-monolith persistence rules
- **One `DbContext` per module, one SQL Server *schema* per module** — `merchant`, `wallet`,
  `keymgmt`, `deposit`, `ledger`, `withdrawal`, `sweep`, `settlement`, `reconciliation`,
  `blockchain`, `platform`. **[Improvement]** SQL Server schemas are first-class (unlike MySQL
  where "schema" == "database"), so all modules share one database with clean logical
  separation. Each module maps via `modelBuilder.HasDefaultSchema("<module>")` and owns its own
  migrations-history table (`__EFMigrationsHistory` in its schema).
- **Default: reference-by-`Guid` across modules; no shared tables** (`CLAUDE.md` §15). A module
  stores another module's key as an opaque `Guid`; cross-module integrity is enforced in the app
  and via events. **[APPROVED §15.5 — guideline, not absolute]** a cross-module FK is allowed as
  a *documented, deliberate exception* where two things are genuinely inseparable — accepting
  that such an FK welds those modules to one physical database (they can't be independently
  extracted) and entangles their migrations. Within a module, FKs are used freely.
- Table names singular, columns PascalCase. `nvarchar` for Unicode (names, descriptions);
  `varchar` for ASCII (addresses, hashes, hex, base-unit money strings).

### 1.7 "Currency" → `AssetId`
- **[FIX]** `DBDesign`'s free-text `Currency` string is replaced everywhere by **`AssetId`
  (Guid ref)** → the `Asset` reference table (§4), which carries chain, symbol, contract
  address, and decimals. This is what makes "add a chain without redesign" (constraint #10) real.

### 1.8 SQL-Server-specific integrity we now use
- **Filtered unique indexes** to enforce single-active invariants declaratively (e.g. one Active
  `WalletAssignment` per wallet): `CREATE UNIQUE INDEX ... WHERE Status = 'Active'`.
  **[Improvement]** — MySQL couldn't do this; it was "app-enforced."
- **CHECK constraints** where cheap and exact (e.g. Ledger "exactly one of Debit/Credit is `0`":
  `CHECK ((Debit = 0 AND Credit > 0) OR (Debit > 0 AND Credit = 0))`).

---

## 2. Module → Schema Map & Phasing

| Module (scaffolded?) | Schema | Phase |
|---|---|---|
| Merchant *(new)* | `merchant` | **P1 core** |
| Wallet ✓ | `wallet` | **P1 core** |
| KeyManagement *(new)* | `keymgmt` | **P1 core** |
| Deposit ✓ | `deposit` | **P1 core** |
| Ledger ✓ | `ledger` | **P1 core** |
| Blockchain ✓ (Mongo + Asset ref) | `blockchain` / Mongo | **P1 core** |
| Withdrawal *(new)* | `withdrawal` | P2 |
| Sweep *(new)* | `sweep` | P2 |
| Settlement *(new)* | `settlement` | P3 |
| Reconciliation *(new)* | `reconciliation` | P3 |
| Platform/Ops (Audit, Config, BackgroundJob) | `platform` | P2 |

"P1 core" = the money-IN path (merchant onboards → wallet derived → deposit detected → ledger
credited). This spec covers all phases so the shape is settled once; we implement per phase,
each reviewed before the next.

---

## 3. Reference Data — Chain
`Chain` is a fixed enum, not a table: `Tron`, `Ethereum`, `Solana` (Bitcoin reserved — needs
UTXO work per `CLAUDE.md` §8). Stored as `nvarchar(16)` (readable, stable) or `tinyint`.

---

## 4. Reference Data — Asset  *(schema `blockchain`, [§15.2])*

Assets are chain metadata (§7.2 "keep the display decimal as chain metadata"), so the catalog
lives in the **Blockchain** module and is exposed via `Blockchain.Contracts` (`IAssetCatalog`).
Ledger/Deposit/etc. store `AssetId` as an opaque unit-of-account key and read `Decimals` only at
the edge — so Ledger still "doesn't know TRON exists," it just balances per `AssetId`.

**Asset**
| Field | Type | Notes |
|---|---|---|
| AssetId | uniqueidentifier (PK) | Guid v7 |
| Chain | nvarchar(16) | Tron / Ethereum / Solana |
| Symbol | nvarchar(16) | TRX, USDT, ETH, SOL, USDC |
| ContractAddress | varchar(128)? | null ⇒ native coin |
| Decimals | int | 6 / 9 / 18 — **edge conversion only** |
| IsNative | bit | |
| Status | nvarchar(16) | Active / Disabled |
| CreatedAt | datetimeoffset | |

Unique: `(Chain, Symbol, ContractAddress)`.

---

## 5. Merchant Module  *(schema `merchant`)*

### Merchant
| Field | Type | Notes |
|---|---|---|
| MerchantId | uniqueidentifier (PK) | |
| MerchantCode | nvarchar(64) | unique |
| Name | nvarchar(256) | |
| CallbackUrl | nvarchar(512)? | webhook target |
| Status | nvarchar(16) | Pending / Active / Suspended / Closed |
| CreatedAt / UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | |

**[FIX] API credentials are never stored as plaintext.** `DBDesign` had `ApiKey`/`ApiSecret`
columns — storing a raw secret violates §10. Replaced by a hashed, rotatable table:

### MerchantApiCredential
| Field | Type | Notes |
|---|---|---|
| CredentialId | uniqueidentifier (PK) | |
| MerchantId | uniqueidentifier (FK, in-module) | |
| ApiKey | varchar(64) | public identifier, unique |
| SecretHash | varchar(256) | **hash only** — HMAC-SHA256 + server-side pepper (secrets are machine-generated high-entropy, so a slow KDF isn't needed) |
| HashVersion | int | **[added]** which pepper produced the hash — an HMAC can't be re-derived without the raw secret, so without this a pepper rotation would permanently lock out every merchant |
| Status | nvarchar(16) | Active / Revoked |
| CreatedAt | datetimeoffset | |
| RevokedAt | datetimeoffset? | |

Raw secret shown once at creation, never persisted. Supports rotation (multiple Active rows).
Verification is constant-time (`CryptographicOperations.FixedTimeEquals`); an unknown API key still
performs a dummy hash so it cannot be distinguished from a wrong secret by timing.

### MerchantConfiguration
| Field | Type | Notes |
|---|---|---|
| ConfigurationId | uniqueidentifier (PK) | |
| MerchantId | uniqueidentifier (FK, in-module) | unique |
| AutoSweepEnabled | bit | |
| WebhookRetryCount | int | |
| IsEnabled | bit | |
| CreatedAt / UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | |

### MerchantAssetPolicy  *([APPROVED] §15.1 — thresholds/limits are per-asset)*
| Field | Type | Notes |
|---|---|---|
| MerchantAssetPolicyId | uniqueidentifier (PK) | |
| MerchantId | uniqueidentifier (FK, in-module) | |
| AssetId | uniqueidentifier (ref) | |
| SweepThreshold | decimal(38,0) | base units |
| MinimumWithdrawal | decimal(38,0) | |
| MaximumWithdrawal | decimal(38,0)? | **[changed]** NULL = no cap. Non-null would force `0` to mean "unlimited" |
| WithdrawalFee | decimal(38,0) | |
| CreatedAt / UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | **[added]** mutable table, per §1.4 |

Unique: `(MerchantId, AssetId)`. CHECK `CK_MerchantAssetPolicy_NonNegative` and
`CK_MerchantAssetPolicy_WithdrawalRange` enforce non-negative amounts and `Max >= Min` in the
database, so a raw-SQL write cannot bypass the domain rules.

### MerchantWebhook  *(outgoing delivery tracking; `Seq` clustered)*
| Field | Type | Notes |
|---|---|---|
| WebhookId | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| MerchantId | uniqueidentifier (FK, in-module) | |
| EventType | nvarchar(64) | |
| Payload | nvarchar(max) | merchant's own event data (JSON) |
| Status | nvarchar(16) | Pending / Delivered / Failed / Exhausted |
| RetryCount | int | |
| NextRetryAt | datetimeoffset? | |
| LastResponse | nvarchar(1024)? | truncated; never logs secrets |
| CreatedAt / UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | **[added]** retry workers update these rows concurrently (§1.4) |

Filtered index `(Status, NextRetryAt) WHERE NextRetryAt IS NOT NULL` serves the retry worker.

---

## 6. Wallet Module  *(schema `wallet`)* — **BUILT**

Owns the derived-address registry and merchant assignment. Holds **no** keys — only an opaque
`DerivedKeyId` issued by KeyManagement, plus a copy of the public address for deposit scanning.

### Wallet
| Field | Type | Notes |
|---|---|---|
| WalletId | uniqueidentifier (PK, clustered) | low-write table |
| DerivedKeyId | uniqueidentifier (ref → keymgmt) | **[changed]** opaque handle, not HDWalletId/DerivationIndex — which key backs this wallet is custody's concern, resolved without leaking the index |
| MerchantId | uniqueidentifier? (ref) | denormalized current holder (from active WalletAssignment); null for platform wallets |
| Chain | nvarchar(16) | a wallet is per-chain (holds native + tokens) — **not** per-asset |
| Address | varchar(128) | copied from the derivation so scanning needs no cross-module call |
| WalletType | nvarchar(24) | **[changed]** Deposit / HotWithdrawal / Treasury / Cold / Energy (dropped ambiguous "Core"/"SharedDeposit") |
| Status | nvarchar(16) | Active / Disabled |
| Description | nvarchar(256)? | |
| CreatedAt / UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | |

Unique: `(Chain, Address)`, `(DerivedKeyId)`. Filtered index `(MerchantId) WHERE MerchantId IS NOT NULL`.
**No `PrivateKey`/`Mnemonic`/`Seed`/`Secret` column — ever.**

**Deposit model — dedicated addresses.** A deposit wallet is one address for one merchant, for life;
reassigning to a different merchant is forbidden (a late payment to the old address would credit the
wrong account). Shared-address-plus-memo (`SharedDeposit`) is deferred and needs `IMemoResolver`
(§8). Platform wallets (Treasury/HotWithdrawal/Cold/Energy) have `MerchantId = NULL` and no assignment.

**Provisioning flow (implemented):** `WalletProvisioningService` verifies the merchant can transact
(`Merchant.Contracts.IMerchantDirectory`), calls `KeyManagement.Contracts.IWalletDerivation` to
allocate+derive atomically, then stores `Wallet` + its first `WalletAssignment`. Two modules, two
transactions: a failed Wallet write orphans the derived key (harmless — an address nobody points at),
never a reuse. Verified end-to-end against real SQL Server: provisioning yields the published BIP-44
vector address.

### WalletAssignment  *(authoritative merchant↔wallet history; `Seq` clustered)*
| Field | Type | Notes |
|---|---|---|
| WalletAssignmentId | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| WalletId | uniqueidentifier (FK, in-module) | |
| MerchantId | uniqueidentifier (ref) | |
| Status | nvarchar(16) | Active / Released |
| AssignedAt | datetimeoffset | |
| ReleasedAt | datetimeoffset? | |

**Filtered unique index** `(WalletId) WHERE Status = 'Active'` → at most one active assignment
per wallet, enforced by the DB. `Wallet.MerchantId` caches the current active row.

---

## 7. KeyManagement & Signing Module  *(schema `keymgmt`)*

Dedicated module (owner's choice). Owns HD-wallet references, signing policy/capabilities, and
the immutable signing audit. **Stores references to secrets, never secrets.** Exposes ports
`ISecretProvider` and `ISigner` (Application/Contracts); Infrastructure implements AWS Secrets
Manager + KMS, swappable for Azure Key Vault / Vault / HSM without touching business logic.

> **BUILT.** `HdWallet` + `DerivedKey` + atomic allocation + secp256k1 derivation are implemented and
> verified against published BIP-39/BIP-44 vectors. The signing tables below (`SigningPolicy`,
> `SigningPolicyLimit`, `WalletSigningCapability`, `SigningRequest`, `SigningApproval`,
> `SigningAudit`) are **specified but not yet created**: they land with the signing implementation,
> once chain adapters exist to produce an unsigned transaction worth signing. Creating tables no
> code writes to is how schemas rot.

### 7.0 Six corrections to the original §7 design
1. **`DerivationScheme` replaces the assumption that every chain derives from an xpub.** ed25519
   (SLIP-0010) is **hardened-only**, so Solana addresses cannot be derived from a public key —
   proven by test (`An_xpub_cannot_derive_a_hardened_child`). §15.4's watch-only derivation holds
   for secp256k1 (Tron, Ethereum) only.
2. **`SigningRequest` gains `AssetId`, `Amount`, `DestinationAddress`.** `SigningPolicy` carried
   `DailyLimit`/`SingleTransactionLimit` with nothing to compare them against — the limits were
   literally unenforceable.
3. **`SigningApproval` table added.** `RequiredApprovals` had nowhere to record an approval;
   `UNIQUE (SigningRequestId, Approver)` stops one operator satisfying a 2-of-N by approving twice.
4. **Filtered unique index `(Chain, Purpose) WHERE Status='Active'`.** "Allocate the next deposit
   address for TRON" was nondeterministic with two active pools.
5. **`NextDerivationIndex` is bounded.** Non-hardened BIP-32 indices are `0 .. 2^31-1`; at `2^31`
   the index silently means *hardened* — a different key. CHECK constraint + domain guard.
6. **`DerivationPath` is validated against `Chain`** (SLIP-44 coin type: 60 ETH / 195 TRON /
   501 SOL). Both ETH and TRON are secp256k1, so a swapped coin type derives valid-looking
   addresses we could never spend from.

Also: **`AccountPublicKey` moved out of the database** into the secret store (`PublicKeyReference`).
An xpub plus *any* single leaked non-hardened child private key mathematically recovers the account
private key, and therefore every address beneath it.

### HdWallet
| Field | Type | Notes |
|---|---|---|
| HdWalletId | uniqueidentifier (PK, clustered) | low-write table |
| Name | nvarchar(128) | |
| Chain | nvarchar(16) | |
| Purpose | nvarchar(16) | Deposit / Withdrawal / Treasury / Energy / Cold |
| Scheme | nvarchar(24) | **[added]** Bip32Secp256k1 / Slip10Ed25519 |
| SecretProvider | nvarchar(32) | AwsSecretsManager / AzureKeyVault / HashiCorpVault / Hsm |
| SecretReference | varchar(512) | ARN / vault path — a **reference**, not the secret |
| PublicKeyReference | varchar(512)? | **[changed]** reference to the account xpub *in the secret store*, not the xpub itself |
| DerivationPath | varchar(64) | `m/44'/195'/0'/0` (secp256k1 branch) or `m/44'/501'` (ed25519 root) |
| NextDerivationIndex | bigint | allocated atomically (§7.6) |
| Status | nvarchar(16) | Active / Archived / Disabled |
| Description | nvarchar(256)? | |
| CreatedAt / UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | |

- Filtered unique index `(Chain, Purpose) WHERE Status='Active'`.
- CHECK `CK_HdWallet_DerivationIndex_Range`: `0 <= NextDerivationIndex <= 2147483648`.
- CHECK `CK_HdWallet_PublicKeyReference_MatchesScheme`: a secp256k1 wallet **must** carry an xpub
  reference; an ed25519 wallet **must not** (it cannot derive from one).

### DerivedKey  *(`Seq` clustered)* — **[added]**
The authoritative "address X is index I of HD wallet W". Lives in custody, not Wallet, so signing
resolves which key to use without reading another module's table.

| Field | Type | Notes |
|---|---|---|
| DerivedKeyId | uniqueidentifier (PK, non-clustered) | the opaque handle Wallet stores |
| Seq | bigint IDENTITY (clustered) | |
| HdWalletId | uniqueidentifier (FK, in-module) | |
| DerivationIndex | bigint | |
| Chain | nvarchar(16) | |
| Address | varchar(128) | public address only — never a key |
| DerivationPath | varchar(80) | full path, e.g. `m/44'/195'/0'/0/17`, so an operator holding the mnemonic can reproduce this exact key |
| CreatedAt | datetimeoffset | |

- UNIQUE `(HdWalletId, DerivationIndex)` — a reused index would give two merchants one address.
- UNIQUE `(Chain, Address)` — defence in depth; this constraint is what caught the deliberately
  broken allocator during mutation testing.
- CHECK `0 <= DerivationIndex <= 2147483647`.

> Everything from here to §7.5 is **specified, not yet built.** It lands with the signing
> implementation. Do not create these tables before there is code that writes to them.

### SigningPolicy
| Field | Type | Notes |
|---|---|---|
| SigningPolicyId | uniqueidentifier (PK) | |
| Name | nvarchar(64) | |
| AutoApprove | bit | |
| RequiredApprovals | int | see `SigningApproval` |
| Status | nvarchar(16) | Active / Disabled |

### SigningPolicyLimit — **[added, correction #2]**
Limits are per-asset for the same reason merchant thresholds are (§15.1): one scalar cannot mean
both TRX and USDT.

| Field | Type | Notes |
|---|---|---|
| SigningPolicyLimitId | uniqueidentifier (PK) | |
| SigningPolicyId | uniqueidentifier (FK, in-module) | |
| AssetId | uniqueidentifier (ref) | |
| DailyLimit | decimal(38,0)? | base units; null ⇒ no cap |
| SingleTransactionLimit | decimal(38,0)? | |

Unique: `(SigningPolicyId, AssetId)`.

### WalletSigningCapability
| Field | Type | Notes |
|---|---|---|
| CapabilityId | uniqueidentifier (PK) | |
| DerivedKeyId | uniqueidentifier (ref) | **[changed]** keyed on the custody handle, not WalletId |
| SigningPolicyId | uniqueidentifier? (FK, in-module) | |
| CanDeposit / CanWithdraw / CanSweep / CanStake / CanDelegateEnergy / CanVote / CanTreasuryTransfer | bit | |
| Status | nvarchar(16) | Active / Disabled |

### SigningRequest  *(`Seq` clustered)*
| Field | Type | Notes |
|---|---|---|
| SigningRequestId | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| DerivedKeyId | uniqueidentifier (ref) | **[changed]** which key signs is resolved inside custody |
| Chain | nvarchar(16) | |
| RequestType | nvarchar(24) | Withdrawal / Sweep / TreasuryTransfer / EnergyDelegation / Stake / Vote |
| AssetId | uniqueidentifier (ref) | **[added, correction #2]** |
| Amount | decimal(38,0) | **[added]** base units — without it, policy limits compare against nothing |
| DestinationAddress | varchar(128) | **[added]** the *intent*, so policy evaluates meaning, not opaque bytes |
| PayloadHash | varchar(128) | hash of the unsigned tx — never a key |
| IdempotencyKey | varchar(128) | unique — dedups signing |
| Status | nvarchar(16) | Pending / Approved / Signed / Broadcast / Rejected / Failed |
| BlockchainTransactionHash | varchar(128)? | |
| RequestedAt | datetimeoffset | |
| CompletedAt | datetimeoffset? | |
| RowVersion | rowversion | |

Unique: `IdempotencyKey`. Index: `DerivedKeyId`, `Status`.

**Never build a blind signing oracle.** The signer must receive the structured intent above *and*
independently confirm the payload decodes to it before signing. A signer that will sign any 32-byte
digest handed to it is a fund-draining primitive, and policy cannot inspect what it approves.

### SigningApproval — **[added, correction #3]**
| Field | Type | Notes |
|---|---|---|
| SigningApprovalId | uniqueidentifier (PK) | |
| SigningRequestId | uniqueidentifier (FK, in-module) | |
| Approver | nvarchar(128) | |
| ApprovedAt | datetimeoffset | |

Unique `(SigningRequestId, Approver)` — otherwise one operator satisfies a 2-of-N policy by
approving twice. Signing is refused until `COUNT(approvals) >= SigningPolicy.RequiredApprovals`.

### SigningAudit  *(immutable, append-only; `Seq` clustered)*
| Field | Type | Notes |
|---|---|---|
| SigningAuditId | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| SigningRequestId | uniqueidentifier (FK, in-module) | |
| WalletId | uniqueidentifier (ref) | |
| Operator | nvarchar(128) | |
| Result | nvarchar(16) | Success / Failure |
| ErrorMessage | nvarchar(1024)? | |
| SignatureHash | varchar(128)? | |
| Duration | int | ms |
| CreatedAt | datetimeoffset | no UpdatedAt |

### SecretReference *(optional — non-wallet secrets: API keys, JWT signing, etc.)*
`SecretReferenceId` (PK), `Name` nvarchar(128), `Provider` nvarchar(32), `Reference`
varchar(512), `Category` nvarchar(64), `CreatedAt` datetimeoffset. References only, never secrets.

### 7.6 Address-derivation flow — **single transaction** (IMPLEMENTED)
Because custody owns `DerivedKey`, index allocation and the key record commit **together**. This is
strictly better than the earlier allocate-then-insert design: a rollback un-consumes the index, so
we get **neither a gap nor a reuse**.

```
Wallet module: "derive next deposit address for chain C"
  → KeyManagement IWalletDerivation.AllocateNext(chain, purpose):

      fetch account xpub from ISecretProvider   ← cached; NO seed, and outside the transaction
      BEGIN TRAN
        UPDATE keymgmt.HdWallet
          SET NextDerivationIndex = NextDerivationIndex + 1
          OUTPUT deleted.NextDerivationIndex          ← atomic; row lock serialises callers
          WHERE Id=@id AND Status='Active' AND NextDerivationIndex <= 2147483647
        publicKey = CKDpub(xpub, index)               ← BIP-32, secp256k1, no key material
        address   = IAddressEncoder(chain).Encode(publicKey)
        INSERT keymgmt.DerivedKey(...)                ← UNIQUE (HdWalletId, index), (Chain, Address)
      COMMIT
      → returns DerivedKeyId (opaque), Address, index, full path
  → Wallet module: INSERT wallet.Wallet(DerivedKeyId, Address, …)   [own tx]
```

Verified under 32-way concurrency: every caller receives a distinct index. Mutation-tested — a naive
read-modify-write allocator makes two callers derive the *same* address, and the
`UNIQUE (Chain, Address)` index rejects it.

**Never reuse an index.** A gap is harmless (an address nobody used); a reuse gives two merchants
the same deposit address and silently misattributes every payment to it — irreversibly, since no
ledger entry can undo an on-chain transfer.

**[APPROVED] §15.4, with a correction:** watch-only derivation (xpub, no mnemonic) works for
**secp256k1 only** — Tron and Ethereum. Solana is ed25519/SLIP-0010, which is **hardened-only**:
`CKDpub` does not exist for it, so its addresses require seed access. Its deriver is deliberately
*not registered* rather than throwing, so an ed25519 wallet fails allocation cleanly
(`keymgmt.scheme_not_supported`) instead of quietly reaching for the seed.

### 7.7 Signing flow *(specified, not yet built)*
```
Withdrawal/Sweep requested → SigningRequest(Pending, intent: asset+amount+destination)
  → policy + approval check (SigningPolicyLimit, SigningApproval)
  → ISigner.Sign(derivedKeyId, unsignedTx):
        verify the payload decodes to the recorded intent   ← never sign an opaque digest
        HdWallet.SecretReference → ISecretProvider.GetAsync → SecretLease (byte[], zeroized)
        derive child private key in memory → sign → dispose the lease
  → SigningRequest(Signed) + SigningAudit(append) → broadcast (Blockchain module)
```
Derived private keys are never persisted or logged. Secrets are `byte[]` behind a `SecretLease`,
never `string` — a .NET string cannot be overwritten.

---

## 8. Deposit Module  *(schema `deposit`)* — BUILT

### Deposit  *(`Seq` clustered)* — as implemented
| Field | Type | Notes |
|---|---|---|
| Id | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| Chain | nvarchar(16) | |
| Address | varchar(128) | the watched deposit address |
| WalletId | uniqueidentifier (ref) | resolved via `IWalletDirectory` |
| MerchantId | uniqueidentifier (ref) | |
| AssetId | uniqueidentifier (ref) | |
| Amount | decimal(38,0) | base units |
| TransactionHash | varchar(128) | |
| OutputIndex | int | output/log index within the tx |
| BlockNumber | bigint | |
| BlockHash | varchar(128) | re-checked each pass; a changed hash ⇒ reorg/orphan |
| Status | nvarchar(16) | Detected / Confirmed / Orphaned / Ignored |
| Confirmations | int | |
| DetectedAt · ConfirmedAt? · CreatedAt · UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | |

**Idempotency / dedup (DB is the arbiter, §7.3):** UNIQUE `UX_Deposit_Tx (Chain, TransactionHash, OutputIndex)`.
Working-set index `IX_Deposit_Chain_Status`; `IX_Deposit_Merchant`.

### ScanCursor — resumable scan watermark (§9)
`Chain` (PK), `LastScannedBlock` bigint, `UpdatedAt`. One row per chain; the scanner advances it only
after a whole block window is processed, so a crash re-scans, never skips.

**Behaviour.** Deposit **never computes balances**. Credit happens only at the policy threshold, never
on first sight — a pre-confirmation reorg costs nothing. At the threshold it publishes `DepositConfirmed`
(via the outbox) → Ledger credits. On reorg it marks `Orphaned` and, **only if it had been confirmed**,
publishes `DepositOrphaned` → Ledger posts a compensating entry (never edits history). Dust below the
per-chain `MinDepositBaseUnits` is recorded `Ignored`, never credited. Per-chain **DepositPolicy**
(`CreditStrategy` Confirmations|Finalized, `Confirmations`, `MinDepositBaseUnits`) comes from config;
a missing policy fails loud (no silent credit default). Chain I/O is behind Blockchain's
`IDepositScanner`/`IChainStatusReader` capability ports — `InMemoryChainSource` today, JSON-RPC later,
swapped by DI (§8 of CLAUDE.md).

---

## 9. Ledger Module  *(schema `ledger`)* — the financial source of truth

Knows nothing of chains; balances everything per opaque `AssetId`. Append-only.
**[APPROVED]** balancing is **per asset**.

### Account
| Field | Type | Notes |
|---|---|---|
| AccountId | uniqueidentifier (PK) | |
| AccountType | nvarchar(32) | MerchantLiability / TreasuryAsset / FeeRevenue / NetworkFeeExpense / … |
| OwnerType | nvarchar(16) | Merchant / Treasury / System |
| OwnerId | uniqueidentifier? (ref) | null for system accounts |
| AssetId | uniqueidentifier (ref) | an account is per-asset |
| NormalSide | nvarchar(8) | Debit / Credit |
| Status | nvarchar(16) | Active / Frozen / Closed |
| CreatedAt | datetimeoffset | |
| RowVersion | rowversion | |

Unique: `(OwnerType, OwnerId, AssetId, AccountType)` — prevents duplicate accounts.

### Journal  *(one financial event; append-only, immutable; `Seq` clustered)*
| Field | Type | Notes |
|---|---|---|
| JournalId | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| ReferenceType | nvarchar(24) | Deposit / DepositReversal / Withdrawal / Sweep / Settlement / Adjustment |
| ReferenceId | uniqueidentifier | the business event id |
| AssetId | uniqueidentifier (ref) | **single asset per journal** |
| MerchantId | uniqueidentifier? (ref) | **[APPROVED]** denormalised reporting dimension = owner of the merchant-side line; null for platform-internal events |
| Description | nvarchar(512) | |
| CreatedAt | datetimeoffset | no UpdatedAt |

**Idempotent posting:** UNIQUE `(ReferenceType, ReferenceId)` — a business event posts exactly
one journal, so replays/retries can't double-credit. Named `UX_Journal_Reference`.

**`MerchantId` on Journal (reporting).** Merchant weekly/monthly statements and the ops team's
per-period balance checks are a core, frequent job. Rather than walk every journal→entry→account
chain to attribute a journal to a merchant, we denormalise the merchant onto the journal header,
immutable and set once at posting. It is **never** a source of balance — a merchant's balance still
derives only from its `MerchantLiability` account's entries — it is purely a filter key, indexed
`IX_Journal_Merchant_CreatedAt (MerchantId, CreatedAt) WHERE MerchantId IS NOT NULL`.

### JournalEntry  *(double-entry lines; append-only, immutable; `Seq` clustered)*
| Field | Type | Notes |
|---|---|---|
| EntryId | uniqueidentifier (PK, non-clustered) | |
| Seq | bigint IDENTITY (clustered) | |
| JournalId | uniqueidentifier (FK, in-module) | |
| AccountId | uniqueidentifier (FK, in-module) | |
| AssetId | uniqueidentifier (ref) | must equal Journal.AssetId |
| Debit | decimal(38,0) | base units; unsigned |
| Credit | decimal(38,0) | base units; unsigned |
| CreatedAt | datetimeoffset | no UpdatedAt |

CHECK: `((Debit = 0 AND Credit > 0) OR (Debit > 0 AND Credit = 0))` — a line is a debit xor a
credit. Index: `JournalId`, `AccountId`.

**Invariants (enforced in the domain before commit):**
- Within a Journal, `Σ Debit == Σ Credit` for that Journal's `AssetId`. **[FIX]** global
  "debits==credits" is wrong across currencies; balancing is per-asset, kept simple with **one
  asset per journal** (cross-asset FX is a future, explicitly-designed case).
- Every line has exactly one of Debit/Credit non-zero.
- Never updated or deleted; corrections are new compensating journals.

### AccountBalance  *(cache only — never source of truth)*
| Field | Type | Notes |
|---|---|---|
| AccountId | uniqueidentifier (PK, FK in-module) | |
| Balance | decimal(38,0) | base units |
| LastEntryId | uniqueidentifier? | rebuild watermark |
| UpdatedAt | datetimeoffset | |
| RowVersion | rowversion | |

Maintained **incrementally** in the same transaction as each journal posting (BigInteger math
in the app). Fully rebuildable from `JournalEntry` — either a SQL `SUM` over the now-numeric
`decimal(38,0)` columns, or app-side `BigInteger` summation. Dropping/rebuilding it loses no truth.

---

## 10. Withdrawal / Sweep / Settlement / Reconciliation  *(P2–P3, spec only)*

### Withdrawal  *(schema `withdrawal`; `Seq` clustered)*
`WithdrawalId` (PK), `Seq`, `MerchantId`/`WalletId`/`AssetId` (ref), `DestinationAddress`
varchar(128), `Amount`/`Fee` decimal(38,0), `IdempotencyKey` varchar(128) **UNIQUE** (client-supplied,
§7.3), `SigningRequestId` uniqueidentifier? (ref → keymgmt), `Status`
(Requested/Approved/Signing/Broadcast/Confirmed/Failed/Rejected), `ApprovedBy` nvarchar(128)?
(required above threshold, §10), `BlockchainTransactionHash` varchar(128)?, `RequestedAt`/
`BroadcastedAt`/`CompletedAt`/`CreatedAt` datetimeoffset, `RowVersion`.

### Sweep  *(schema `sweep`)*
`SweepId` (PK), `FromWalletId`/`ToWalletId` (ref), `AssetId`, `Amount`, `Fee?`,
`BlockchainTransactionHash?`, `SigningRequestId?` (ref), `Status`, `Reason`, `CreatedAt`,
`CompletedAt`, `RowVersion`.

### WalletTransfer  *(schema `sweep` — internal/treasury moves)*
`TransferId` (PK), `FromWalletId`/`ToWalletId` (ref), `AssetId`, `Amount`, `Reason`,
`BlockchainTransactionHash?`, `SigningRequestId?` (ref), `Status`, `CreatedAt`, `RowVersion`.

### Settlement  *(schema `settlement`)*
`SettlementId` (PK), `MerchantId` (ref), `AssetId`, `SettlementPeriod`, `Amount`, `Status`,
`SettledAt`, `CreatedAt`, `RowVersion`.

### ReconciliationJob / ReconciliationIssue  *(schema `reconciliation`)*
- Job: `JobId` (PK), `Chain`, `StartBlock`, `EndBlock`, `Status`, `StartedAt`, `CompletedAt`.
- Issue: `IssueId` (PK), `JobId` (FK in-module), `WalletId` (ref), `TransactionHash`,
  `IssueType`, `ExpectedValue`/`ActualValue` decimal(38,0), `ResolutionStatus`, `ResolvedAt`,
  `CreatedAt`.

---

## 11. Platform / Ops & Per-Module Infrastructure Tables

### 11.1 Outbox — **per module** (each module's schema, `Seq` clustered)
Written in the same transaction as the business change (§7.5). Reconciles the already-built
`Infrastructure/Outbox/OutboxMessage`:
| Field | Type | Notes |
|---|---|---|
| Id | uniqueidentifier (PK, non-clustered) | = IntegrationEvent.EventId |
| Seq | bigint IDENTITY (clustered) | |
| Type | nvarchar(512) | assembly-qualified event type |
| Content | nvarchar(max) | serialized event (JSON) |
| OccurredOnUtc | datetimeoffset | |
| ProcessedOnUtc | datetimeoffset? | |
| RetryCount | int | |
| Error | nvarchar(2048)? | last dispatch error |
| CreatedAt | datetimeoffset | |

### 11.2 Idempotency — **per module** (owned by the module whose write it guards, §7.3)
| Field | Type | Notes |
|---|---|---|
| IdempotencyKey | varchar(128) (PK) | client `Idempotency-Key` |
| RequestHash | varchar(128) | detects key reuse with a different body |
| Response | nvarchar(max) | stored result returned on replay |
| Status | nvarchar(16) | InProgress / Completed |
| CreatedAt | datetimeoffset | |
| ExpiresAt | datetimeoffset | |

### 11.3 Platform schema (`platform`) — cross-cutting ops
- **AuditLog** *(immutable, `Seq` clustered)*: `AuditId` (PK), `User`, `Action`, `Entity`,
  `EntityId`, `Before` nvarchar(max), `After` nvarchar(max), `CreatedAt`.
- **Configuration**: `Key` varchar(128) (PK), `Value` nvarchar(max), `Category`, `UpdatedAt`.
- **BackgroundJob**: `JobId` (PK), `JobType`, `Status`, `RetryCount`, `LastRun`, `NextRun`,
  `UpdatedAt`. (Worker cursors/heartbeats per §9.)

---

## 12. MongoDB Collections  *(blockchain/external state only — never money truth)*

Owned by the **Blockchain** module Infrastructure (resource/energy ones by Blockchain/Resources
or the future Energy module). **No secrets, ever. Append-only where practical.** Money is never
reconstructed from Mongo (`CLAUDE.md` §2).

| Collection | Purpose | Owner |
|---|---|---|
| WalletSnapshot | latest on-chain balances | Blockchain |
| BlockchainTransaction | raw tx | Blockchain |
| TransactionReceipt | raw receipt/logs | Blockchain |
| ContractEvent | contract event logs | Blockchain |
| Block | raw block | Blockchain |
| RpcLog | every RPC request/response + timing | Blockchain |
| WebhookLog | inbound provider callbacks (verify + idempotent before side effects) | Blockchain |
| AddressMetadata | address permissions/activation/raw | Blockchain |
| WalletResource | current TRON energy/bandwidth/frozen | Blockchain/Resources |
| WalletResourceHistory | historical resource snapshots | Blockchain/Resources |
| EnergyDelegation | TRON energy delegation records | Blockchain/Resources |

---

## 13. Consolidated Money & Ledger Invariants
1. All amounts are base-unit `BigInteger`, stored as `DECIMAL(38,0)` integers (scale 0); convert
   to display only at the edge using `Asset.Decimals`.
2. Every financial movement → exactly one `Journal` (UNIQUE `ReferenceType+ReferenceId`) with
   balanced `JournalEntry` lines per asset.
3. `Σ Debit == Σ Credit` per journal per asset.
4. Merchant balances derive from `JournalEntry`, never from `Deposit`/`Withdrawal` tables.
5. `AccountBalance` is a rebuildable, incrementally-maintained cache.
6. Ledger history is append-only; corrections are compensating journals.
7. Deposits credit only after N confirmations; orphaned tx → compensating entry.
8. On-chain dedup is a DB UNIQUE constraint, not app logic.

## 14. Consolidated Security Rules (KMS doc + §10)
- No column for `PrivateKey` / `Mnemonic` / `Seed` / `Secret` / `WalletPassword` in any store.
- SQL Server / Mongo / Redis hold only: address, `HDWalletId`, derivation index/path, secret
  **provider + reference**, wallet metadata, `AccountPublicKey`.
- Mnemonic is fetched (via `ISecretProvider`) only for signing, derived in memory, zeroized
  immediately; derived keys never persisted or logged.
- API secrets stored **hashed** (`MerchantApiCredential`), never plaintext.
- `NextDerivationIndex` allocated atomically; gaps OK, reuse forbidden.
- Provider swappable (`ISecretProvider`) — AWS SM/KMS today; Azure/Vault/HSM later; no schema
  or business-logic change.

---

## 15. Decision Log (all resolved)

| # | Decision | Outcome |
|---|---|---|
| 15.1 | Per-asset thresholds/limits (`MerchantAssetPolicy`, `SigningPolicy.AssetId`) | **APPROVED** |
| 15.2 | `Asset`/`Chain` catalog home | **Blockchain module** (`IAssetCatalog`); Ledger treats `AssetId` as opaque |
| 15.3 | API credentials | **Hashed, rotatable `MerchantApiCredential`** — HMAC-SHA256 + server pepper; no plaintext |
| 15.4 | `HDWallet.AccountPublicKey` so addresses derive without the mnemonic | **APPROVED** |
| 15.5 | Cross-module FKs | **Guideline:** default reference-by-`Guid`; cross-module FK only as a documented, deliberate exception |
| 15.6 | GUID clustering | **Approved, per-table:** append-heavy tables get non-clustered GUID PK + `bigint IDENTITY Seq` clustered; low-write tables keep clustered GUID PK |
| Money | Storage type | **`DECIMAL(38,0)`** base-unit integers (scale 0); `BigInteger ↔ SqlDecimal` converter; reject >38-digit amounts at ingestion |

Implementation order — **P1**: `SharedKernel`/`Infrastructure` persistence primitives
(`BigInteger↔SqlDecimal` converter, GUID-v7 + `Seq` base entity map, per-module `DbContext` base,
outbox interceptor wiring, SQL Server schema defaults) → Blockchain `Asset` → Merchant →
KeyManagement → Wallet → Deposit → Ledger. Each module gets its own migration and is reviewed
before the next.
