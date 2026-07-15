# Deposit Flow — Complete Technical & Visual Documentation

## 1. Architecture Overview

### Module Dependencies

```
┌─────────────────────────────────────────────────────────────────┐
│                         MerchantGateway                         │
│                      (Composition Root)                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │  Blockchain  │  │   KeyMgmt    │  │ Merchant (Registry)  │  │
│  │   (Chains)   │  │  (Custody)   │  │                      │  │
│  └───────┬──────┘  └──────┬───────┘  └──────────┬───────────┘  │
│          │                │                      │             │
│          └────────────────┴──────────────────────┘             │
│                          │                                      │
│          ┌───────────────┴────────────────┐                    │
│          ▼                                ▼                    │
│  ┌───────────────────┐  ┌───────────────────────┐              │
│  │  AssetManagement  │  │  PaymentProcessing    │              │
│  │    (Wallet)       │  │   (Deposit/Withdrawal)│              │
│  └───────────────────┘  └───────────┬───────────┘              │
│          │                          │                          │
│          └──────────────┬───────────┘                          │
│                         ▼                                      │
│             ┌────────────────────────┐                        │
│             │  Financial (Ledger)    │                        │
│             │  (Double-Entry Journal)│                        │
│             └────────────────────────┘                        │
│                                                                 │
│  Legend:                                                        │
│  • Blockchain: Reads chain state (RPC, scanning)              │
│  • KeyMgmt: Derives addresses, holds key references           │
│  • Wallet: Tracks deposit addresses per merchant              │
│  • PaymentProcessing: Detects transfers, tracks confirmations │
│  • Ledger: Posts money-in/out via double-entry journals       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## 2. Domain Model: The Deposit Aggregate

### Deposit Entity (Aggregate Root)

**File:** `PaymentProcessing/Deposit/Domain/Deposit.cs`

```csharp
public sealed class Deposit : Entity<Guid>
{
    public Chain Chain { get; }                    // Immutable
    public string Address { get; }                 // Immutable (watched address)
    public Guid WalletId { get; }                  // Immutable
    public Guid MerchantId { get; }                // Immutable
    public Guid AssetId { get; }                   // Immutable
    
    public BigInteger Amount { get; }              // Immutable (base units)
    public string TransactionHash { get; }         // Immutable
    public int OutputIndex { get; }                // Immutable (log index)
    
    public long BlockNumber { get; }               // Immutable
    public string BlockHash { get; }               // Mutable (can change in reorg)
    
    public DepositStatus Status { get; }           // Mutable: Detected → Confirmed/Orphaned
    public int Confirmations { get; }              // Mutable (updated on each pass)
    
    public DateTimeOffset DetectedAt { get; }      // When first seen on chain
    public DateTimeOffset? ConfirmedAt { get; }    // When credit threshold was met
}
```

### Deposit State Machine

```
                    ┌──────────────────────────────┐
                    │        IGNORED (Dust)        │
                    │  Below minimum deposit       │
                    │  • Never credited            │
                    │  • Kept for audit            │
                    └──────────────────────────────┘
                              △
                              │
                      [Record] Amount < MinDeposit
                              │
                              │
    ┌─────────────────────────────────────────────────────┐
    │                      START                          │
    │             (Transfer found on-chain)               │
    └───────────────────────┬─────────────────────────────┘
                            │
                            │
                    [Record] Amount >= MinDeposit
                            │
                            ▼
                    ┌──────────────────────────────┐
                    │       DETECTED (Pending)     │
                    │  • Seen on-chain             │
                    │  • Confirmations: 0→N        │
                    │  • NOT yet credited          │
                    │  • Watching for reorg        │
                    └──────────────────────────────┘
                            │
                    ┌───────┴────────┐
                    │                │
        [Reorg]     │                │ [IsCreditable]
        Block gone  │                │ Confirmations >= N
                    │                │ OR isFinalized
                    │                │
                    ▼                ▼
          ┌──────────────────┐   ┌─────────────────┐
          │   ORPHANED       │   │   CONFIRMED     │
          │ • Reorg'd away   │   │ • Credit ready  │
          │ • Was Pending OR │   │ • Ledger credit │
          │   Confirmed      │   │   (Outbox →)    │
          │ • If was Conf'd: │   │ • Event raised  │
          │   Compensating   │   │                 │
          │   entry posted   │   └─────────────────┘
          └──────────────────┘

Correctness Rules:
• Dust is filtered at Record — never credited
• No credit until policy threshold (confirmations + finality)
• Reorg before credit = no ledger impact (silent orphan)
• Reorg AFTER credit = compensating journal entry (DepositOrphaned)
• All transitions idempotent (safe to re-run)
```

### DepositPolicy — Per-Chain Configuration

```csharp
public sealed record DepositPolicy(
    CreditStrategy CreditStrategy,     // Confirmations OR Finalized
    int RequiredConfirmations,         // N blocks deep (if Confirmations)
    BigInteger MinDeposit              // Dust floor in base units
)
{
    public bool MeetsMinimum(BigInteger amount) => amount >= MinDeposit;
    
    public bool IsCreditable(int confirmations, bool isFinalized) => CreditStrategy switch
    {
        CreditStrategy.Confirmations => confirmations >= RequiredConfirmations,
        CreditStrategy.Finalized => isFinalized,
        _ => false
    };
}
```

**Configured from:** `appsettings.json` → `Deposit:Policies:<Chain>`

**Example:**
```json
{
  "Deposit": {
    "Policies": {
      "Tron": {
        "CreditStrategy": "Confirmations",
        "RequiredConfirmations": 30,
        "MinDeposit": "1000000"  // 1 USDT (6 decimals) in sun
      }
    }
  }
}
```

---

## 3. Wallet Module Integration

### How Addresses Are Provisioned

**File:** `AssetManagement/Wallet/Application/WalletProvisioningService.cs`

```
User Request
    │
    ├─ MerchantId, Chain
    │
    ▼
IMerchantDirectory.FindByIdAsync(merchantId)
    │
    ├─ Verify merchant exists and CanTransact=true
    │
    ▼
IWalletDerivation.AllocateNextAsync(chain, DerivationPurpose.Deposit)
    │
    ├─ KeyManagement allocates next index atomically
    ├─ Derives address from HD wallet
    ├─ Returns DerivedAddress (with opaque DerivedKeyId)
    │
    ▼
Wallet.CreateDeposit(derivedKeyId, chain, address, merchantId)
    │
    ├─ Creates Wallet aggregate
    ├─ Seeds active WalletAssignment (merchant → wallet link)
    │
    ▼
DepositRepository.Save()
    │
    └─ Persists in one transaction
        
Result: ProvisionedDepositAddress { WalletId, Chain, Address }
```

**Key Properties:**
- **Dedicated:** One address per merchant (never reused)
- **HD-Derived:** From KeyManagement's HD wallet
- **Custody-Safe:** App never sees the private key (DerivedKeyId is opaque)
- **Idempotent:** Concurrent calls never get the same address

---

## 4. Deposit Detection Flow

### DepositDetectionService (Scans Chain)

**File:** `PaymentProcessing/Deposit/Application/DepositDetectionService.cs`

```
┌─────────────────────────────────────────────────────────────┐
│             DepositScannerWorker (Periodic)                │
│            Runs every ScanInterval seconds                 │
│             (configured: 10s in dev)                       │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
             ScanOnceAsync(Chain)
                │
                ├─ Get current chain tip height
                │
                ├─ Get last scanned block from cursor
                │
                ├─ Calculate scan window:
                │  fromBlock = lastScanned + 1
                │  toBlock = min(tip, fromBlock + 500 - 1)
                │  (Max 500 blocks per pass)
                │
                ├─ IDepositScanner.ScanAsync(fromBlock, toBlock)
                │
                │  ┌─────────────────────────────────┐
                │  │  InMemoryChainSource (dev)      │
                │  │  OR TronChainAdapter (prod)     │
                │  │  OR EthereumAdapter (future)    │
                │  │                                 │
                │  │  Returns:                       │
                │  │  IReadOnlyList<DetectedTransfer>│
                │  └─────────────────────────────────┘
                │
                ├─ For each detected transfer:
                │
                │  ├─ IWalletDirectory.FindByAddressAsync()
                │  │  "Is this address ours?"
                │  │
                │  ├─ Reject if:
                │  │  • Not in directory (not ours)
                │  │  • Inactive wallet
                │  │  • No merchant assigned
                │  │  • Not a Deposit wallet
                │  │
                │  ├─ Deposit.Record(...)
                │  │  Creates domain aggregate
                │  │  • Check address, tx hash, amount > 0, owners not empty
                │  │  • If amount < MinDeposit → Status = Ignored
                │  │  • Else → Status = Detected
                │  │
                │  ├─ IDepositRepository.AddIfNewAsync()
                │  │  UNIQUE(Chain, TxHash, OutputIndex) dedup
                │  │  • Recorded = new
                │  │  • Duplicate = already seen (re-scan)
                │  │
                │  └─ If Recorded: recorded++
                │
                ├─ Advance cursor AFTER whole window
                │  (Crash re-scans, never skips)
                │
                └─ Return recorded count

┌─ Worker logs if recorded > 0
└─ Next pass starts from updated cursor
```

### Deduplication Strategy

```
DB Level:
┌─ UNIQUE INDEX UX_Deposit_Tx
│  ├─ Chain
│  ├─ TransactionHash
│  └─ OutputIndex
│
└─ On duplicate constraint violation:
   ├─ Repository catches SqlException (error 2601/2627)
   ├─ Returns DepositRecordOutcome.Duplicate
   └─ Caller ignores (already processed)
```

---

## 5. Confirmation & Reorg Detection

### DepositConfirmationService (Advances State)

**File:** `PaymentProcessing/Deposit/Application/DepositConfirmationService.cs`

```
┌─────────────────────────────────────────────────────────────┐
│          DepositConfirmationWorker (Periodic)               │
│          Runs every ConfirmationInterval seconds            │
│          (configured: 10s in dev)                           │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
             TrackOnceAsync(Chain)
                │
                ├─ Get all trackable deposits (status Detected/Confirmed)
                │
                ├─ Get current tip height
                ├─ Get finalized height (chain-specific)
                │
                ├─ For each tracked deposit:
                │
                │  ├─ IChainStatusReader.GetBlockAsync(chain, blockNumber)
                │  │  "Is the block still canonical?"
                │  │
                │  ├─ Decision tree:
                │  │
                │  │  IF block == null OR blockHash changed:
                │  │  │
                │  │  │  ├─ deposit.MarkOrphaned(now)
                │  │  │  │
                │  │  │  ├─ IF was Confirmed:
                │  │  │  │  │  ├─ Raise DepositOrphaned event
                │  │  │  │  │  │
                │  │  │  │  │  └─ (Ledger will post compensating entry)
                │  │  │  │  │
                │  │  │  │  └─ Log warning: "reorg deeper than confirmation depth!"
                │  │  │  │
                │  │  │  └─ (If was Detected: silent, nothing to reverse)
                │  │  │
                │  │  ELSE (block still canonical):
                │  │  │
                │  │  ├─ confirmations = tip - blockNumber + 1
                │  │  ├─ isFinalized = blockNumber <= finalizedHeight
                │  │  │
                │  │  ├─ deposit.RegisterConfirmations(confirmations, isFinalized, policy, now)
                │  │  │
                │  │  │  IF policy.IsCreditable(confirmations, isFinalized):
                │  │  │  │
                │  │  │  │  ├─ Status = Confirmed
                │  │  │  │  ├─ ConfirmedAt = now
                │  │  │  │  │
                │  │  │  │  └─ Raise DepositConfirmed event
                │  │  │  │     (Outbox → IEventBus → Ledger handler)
                │  │  │  │
                │  │  │  ELSE:
                │  │  │     (Keep Detected, wait for more confirmations)
                │  │
                │  └─ If Status changed: changed++
                │
                └─ Repository.SaveChangesAsync()
                   (Single commit with all state mutations + outbox events)

┌─ Worker logs changes
└─ Next pass tracks new deposits & confirms pending ones
```

---

## 6. Events & Ledger Integration

### Domain Events (Raised by Deposit Aggregate)

**DepositConfirmed**
```csharp
public sealed record DepositConfirmed(
    Guid EventId,
    DateTimeOffset OccurmedOnUtc,
    Guid DepositId,
    Guid WalletId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,    // BigInteger as string (lossless)
    Chain Chain,
    string TransactionHash,
    int OutputIndex,
    DateTimeOffset ConfirmedAt
) : IDomainEvent, IIntegrationEvent;
```

**DepositOrphaned**
```csharp
public sealed record DepositOrphaned(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid DepositId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    Chain Chain,
    string TransactionHash,
    int OutputIndex,
    DateTimeOffset OrphanedAt
) : IDomainEvent, IIntegrationEvent;
```

### Outbox Pattern (Durability)

```
Deposit Module (Single Transaction):
┌──────────────────────────────────────┐
│                                      │
│  1. Deposit aggregate state mutation │
│     └─ Raise DepositConfirmed event  │
│                                      │
│  2. In same transaction:             │
│     └─ Write event to Outbox table   │
│        ┌─────────────────────────────┤
│        │ OutboxMessage               │
│        ├─────────────────────────────┤
│        │ Id (PK)                     │
│        │ Type = "DepositConfirmed"   │
│        │ Payload (JSON)              │
│        │ CreatedAt                   │
│        │ ProcessedAt (NULL)          │
│        │ Processed (FALSE)           │
│        └─────────────────────────────┘
│
│  3. All-or-nothing COMMIT
│
└──────────────────────────────────────┘
                │
                │ OutboxDispatcher (Background)
                │ Runs periodically
                │
                ▼
┌──────────────────────────────────────┐
│  Outbox Relay                        │
│                                      │
│  1. SELECT unprocessed messages      │
│  2. For each message:                │
│     ├─ Deserialize                  │
│     ├─ IEventBus.PublishAsync()     │
│     │  (In-process for now,         │
│     │   Kafka-ready by contract)    │
│     │                                │
│     ├─ Handler executes             │
│     │  (Deposit→Ledger credit)      │
│     │                                │
│     └─ If success: mark Processed   │
│        If failure: leave for retry  │
│                                      │
└──────────────────────────────────────┘
```

**Key Property:**
- Idempotent: If event is redelivered, the Ledger's `(ReferenceType, ReferenceId)` UNIQUE index deduplicates it
- At-least-once: Message stays in outbox until confirmed processed
- No message lost: If dispatcher crashes, same message retries on next dispatcher run

### Ledger Event Handlers

**File:** `Financial/Ledger/Application/Handlers/DepositEventHandlers.cs`

```
DepositConfirmed (from Outbox)
         │
         ▼
DepositConfirmedHandler : IIntegrationEventHandler<DepositConfirmed>
         │
         ├─ Parse AmountBaseUnits (string → BigInteger)
         │
         ├─ ILedgerPoster.CreditDepositAsync(
         │    DepositId, MerchantId, AssetId, Amount
         │  )
         │
         │  ┌─────────────────────────────────────┐
         │  │ Ledger Posts Double-Entry Journal:  │
         │  │                                     │
         │  │ DEBIT:  PlatformClearingAccount     │
         │  │         (amount)                    │
         │  │                                     │
         │  │ CREDIT: MerchantLiabilityAccount    │
         │  │         (amount)                    │
         │  │                                     │
         │  │ References:                         │
         │  │   ReferenceType = "Deposit"         │
         │  │   ReferenceId = DepositId           │
         │  │                                     │
         │  │ Dedup: (ReferenceType, ReferenceId) │
         │  │        UNIQUE ensures idempotency   │
         │  └─────────────────────────────────────┘
         │
         └─ AccountBalance cache updated
            in same transaction

DepositOrphaned (from Outbox)
         │
         ▼
DepositOrphanedHandler : IIntegrationEventHandler<DepositOrphaned>
         │
         ├─ ILedgerPoster.ReverseDepositAsync(...)
         │
         │  ┌──────────────────────────────────┐
         │  │ Ledger Posts COMPENSATING Entry: │
         │  │                                  │
         │  │ CREDIT:  PlatformClearingAccount │
         │  │          (amount)                │
         │  │                                  │
         │  │ DEBIT:   MerchantLiabilityAccount│
         │  │          (amount)                │
         │  │                                  │
         │  │ (Reverses the credit, keeps     │
         │  │  both entries in ledger)        │
         │  └──────────────────────────────────┘
         │
         └─ Merchant balance unchanged
            (original credit + reversal = net 0)
```

---

## 7. Ledger Double-Entry Integration

### Account Types & Normal Sides

```
Ledger Design:

┌──────────────────────────────────────────────────────┐
│              ACCOUNT STRUCTURE                       │
├──────────────────────────────────────────────────────┤
│                                                      │
│  MERCHANT ACCOUNTS                                  │
│  ─────────────────                                  │
│  MerchantLiabilityAccount(MerchantId, AssetId)     │
│  │                                                  │
│  ├─ What we owe the merchant                       │
│  ├─ Credit-normal (credit side grows balance)      │
│  ├─ CREDIT on deposit confirmed                    │
│  ├─ DEBIT on withdrawal settled                    │
│  └─ This is the merchant's balance read via       │
│     ILedgerQuery.GetMerchantBalanceAsync()         │
│                                                      │
│                                                      │
│  PLATFORM ACCOUNTS                                  │
│  ─────────────────                                  │
│  PlatformClearingAccount(AssetId)                  │
│  │                                                  │
│  ├─ What the platform holds in custody            │
│  ├─ Debit-normal (debit side grows balance)       │
│  ├─ DEBIT on deposit confirmed                     │
│  ├─ CREDIT on withdrawal broadcast                 │
│  └─ Always balanced against merchant liabilities   │
│                                                      │
│                                                      │
│  WITHDRAWAL ACCOUNTS                                │
│  ──────────────────                                │
│  WithdrawalClearingAccount(AssetId)               │
│  │                                                  │
│  ├─ Temporary hold during withdrawal flow         │
│  ├─ Merchant balance decreases (reserved)         │
│  ├─ If approved: moves to broadcast               │
│  ├─ If rejected: reverses                         │
│  └─ CREDIT when withdrawal reserved               │
│                                                      │
└──────────────────────────────────────────────────────┘

Balance Equation (per asset):
┌────────────────────────────────────────────┐
│  MerchantLiability(merchant, asset)        │
│  ÷ PlatformClearing(asset)                 │
│  ────────────────────────────────────────  │
│   Must always sum to zero (balanced)       │
│                                            │
│  If not zero → ledger is corrupt           │
│  (This is caught by the audit report)      │
└────────────────────────────────────────────┘
```

### Journal Entry Structure

```csharp
public sealed class Journal : Entity<Guid>
{
    public JournalReferenceType ReferenceType { get; }  // "Deposit"
    public Guid ReferenceId { get; }                     // DepositId
    
    public Guid AssetId { get; }                         // Single asset per journal
    public Guid? MerchantId { get; }                     // Owner (if applicable)
    
    public IReadOnlyList<JournalEntry> Entries { get; } // Balanced lines
}

// For a deposit credit, the journal looks like:

Journal:
├─ ReferenceType = "Deposit"
├─ ReferenceId = {DepositId}
├─ AssetId = {UsdtAssetId}
├─ MerchantId = {MerchantId}
├─ Description = "Deposit confirmed"
│
└─ Entries (must balance):
   ├─ Entry 1:
   │  ├─ Account = PlatformClearingAccount(UsdtAssetId)
   │  ├─ Direction = Debit
   │  └─ Amount = 1000000 (1 USDT in sun)
   │
   └─ Entry 2:
      ├─ Account = MerchantLiabilityAccount(MerchantId, UsdtAssetId)
      ├─ Direction = Credit
      └─ Amount = 1000000 (1 USDT in sun)
      
Total Debit = 1000000
Total Credit = 1000000
√ BALANCED
```

### Account Balance Cache

```
When a Journal is posted:

1. Validate journal balances
2. Create/get-or-create accounts as needed
3. For each entry:
   ├─ Apply to AccountBalance
   └─ AccountBalance.Balance = 
       (Balance + amount if credit-normal & credit side)
       OR
       (Balance + amount if debit-normal & debit side)

AccountBalance:
├─ Id (FK to Account)
├─ Balance (derived, updated in-transaction)
└─ UpdatedAt

ILedgerQuery reads from AccountBalance cache (never reconstructs):
└─ Fast, consistent with journal it's derived from
```

---

## 8. Background Workers

### DepositScannerWorker

**File:** `PaymentProcessing/Deposit/Workers/DepositScannerWorker.cs`

```
Configuration (Program.cs):
├─ Chains: [Tron]
├─ ScanInterval: 10 seconds
└─ Creates new DbContext per pass

Lifecycle:
├─ Boots with host
├─ Runs PeriodicTimer
│
├─ Each tick:
│  ├─ For each chain:
│  │  ├─ Create service scope (fresh DbContext)
│  │  ├─ DepositDetectionService.ScanOnceAsync()
│  │  └─ Catch + log exceptions
│  │     (other chains continue)
│  │
│  └─ Wait ScanInterval
│
└─ On shutdown: PeriodicTimer.Dispose()
```

### DepositConfirmationWorker

**File:** `PaymentProcessing/Deposit/Workers/DepositConfirmationWorker.cs`

```
Configuration (Program.cs):
├─ Chains: [Tron]
├─ ConfirmationInterval: 10 seconds
└─ Creates new DbContext per pass

Lifecycle:
├─ Boots with host
├─ Runs PeriodicTimer
│
├─ Each tick:
│  ├─ For each chain:
│  │  ├─ Create service scope (fresh DbContext)
│  │  ├─ DepositConfirmationService.TrackOnceAsync()
│  │  │  ├─ Advances pending deposits
│  │  │  ├─ Detects reorgs
│  │  │  ├─ Publishes events to Outbox
│  │  │  └─ Commits single transaction
│  │  │
│  │  └─ Catch + log exceptions
│  │
│  └─ Wait ConfirmationInterval
│
└─ On shutdown: PeriodicTimer.Dispose()
```

### OutboxDispatcher

**File:** `Infrastructure/Outbox/OutboxDispatcher.cs`

```
Runs in-process, separate from the worker loop:

Per-module OutboxDispatcher<DepositDbContext>:
├─ Configuration:
│  ├─ Polling interval (e.g., 5 seconds)
│  └─ Redis lock for single-flight (per module)
│
├─ Each tick:
│  ├─ Acquire Redis lock (distributed, TTL 30s)
│  ├─ If acquired:
│  │  ├─ SELECT unprocessed messages from Outbox
│  │  ├─ For each message:
│  │  │  ├─ Deserialize JSON payload
│  │  │  ├─ IEventBus.PublishAsync(event)
│  │  │  │  (In-memory today; Kafka contract-ready)
│  │  │  │
│  │  │  ├─ If handler succeeds:
│  │  │  │  ├─ Mark Processed = true
│  │  │  │  └─ Commit
│  │  │  │
│  │  │  └─ If handler fails (e.g., ledger error):
│  │  │     ├─ Don't mark Processed
│  │  │     ├─ Leave for retry
│  │  │     └─ Log error
│  │  │
│  │  └─ Release lock
│  │
│  └─ If lock not acquired:
│     └─ Another instance is processing; skip this tick
│
└─ Retry on next tick
```

**Key Property:**
- Single-flight per module (Redis lock prevents duplicate processing)
- At-least-once delivery (unprocessed messages retry forever)
- Idempotent by contract (ledger deduplicates by reference)

---

## 9. Data Model (SQL Server Schema)

### Deposit Table

```sql
CREATE TABLE deposit.Deposit (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    Chain INT NOT NULL,                         -- 0=Tron, 1=Ethereum, 2=Solana
    
    Address NVARCHAR(100) NOT NULL,             -- "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t"
    WalletId UNIQUEIDENTIFIER NOT NULL,         -- FK to Wallet
    MerchantId UNIQUEIDENTIFIER NOT NULL,       -- FK to Merchant (denorm)
    AssetId UNIQUEIDENTIFIER NOT NULL,          -- Opaque asset reference
    
    Amount DECIMAL(38, 0) NOT NULL,             -- 1000000 (base units)
    TransactionHash VARCHAR(255) NOT NULL,      -- "0xabc123..."
    OutputIndex INT NOT NULL,                   -- Log index / vout
    
    BlockNumber BIGINT NOT NULL,                -- 123456789
    BlockHash VARCHAR(255) NOT NULL,            -- May change in reorg
    
    Status INT NOT NULL,                        -- 1=Detected, 2=Confirmed, 3=Orphaned, 4=Ignored
    Confirmations INT NOT NULL DEFAULT 0,       -- Current depth
    
    DetectedAt DATETIMEOFFSET NOT NULL,         -- When scanner first saw it
    ConfirmedAt DATETIMEOFFSET NULL,            -- When policy threshold met
    CreatedAt DATETIMEOFFSET NOT NULL,
    UpdatedAt DATETIMEOFFSET NOT NULL,
    
    -- Deduplication key
    CONSTRAINT UX_Deposit_Tx UNIQUE (Chain, TransactionHash, OutputIndex)
);

CREATE INDEX IX_Deposit_Chain_Status ON deposit.Deposit(Chain, Status);
CREATE INDEX IX_Deposit_Address ON deposit.Deposit(Address);
CREATE INDEX IX_Deposit_WalletId ON deposit.Deposit(WalletId);
CREATE INDEX IX_Deposit_MerchantId ON deposit.Deposit(MerchantId);
```

### Outbox Tables (per module)

```sql
-- Deposit module outbox
CREATE TABLE deposit.OutboxMessage (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    Type NVARCHAR(255) NOT NULL,               -- "DepositConfirmed"
    Payload NVARCHAR(MAX) NOT NULL,            -- JSON serialized event
    CreatedAt DATETIMEOFFSET NOT NULL,
    ProcessedAt DATETIMEOFFSET NULL,
    Processed BIT NOT NULL DEFAULT 0,
    
    CONSTRAINT UX_OutboxMessage_Type_Processed 
        UNIQUE (Type, Processed, Id)          -- For efficient polling
);
```

### Scan Cursor (Resume State)

```sql
CREATE TABLE deposit.ScanCursor (
    Id UNIQUEIDENTIFIER PRIMARY KEY,
    Chain INT NOT NULL,
    LastScannedBlock BIGINT NOT NULL DEFAULT 0,
    UpdatedAt DATETIMEOFFSET NOT NULL,
    
    CONSTRAINT UX_ScanCursor_Chain UNIQUE (Chain)
);
```

---

## 10. Complete End-to-End Sequence

### Happy Path: Deposit Detected → Confirmed → Credited

```
Time: T0  Merchant provisions address
          └─ Wallet created with address TR7NHqje...
          
Time: T1  User sends funds to that address on-chain
          └─ Chain includes transfer in block 12345
          
Time: T2  (T1 + ScanInterval = +10 sec)
          Scanner runs:
          │
          ├─ GetTipHeight() → 12345
          ├─ ScanAsync(0, 500)
          │  └─ Finds transfer to TR7NHqje... with 1 USDT
          │
          ├─ WalletDirectory.FindByAddressAsync()
          │  └─ Found! Merchant #123, Wallet #456
          │
          ├─ Deposit.Record(...)
          │  └─ Status = Detected
          │
          ├─ Repository.AddIfNewAsync()
          │  └─ Inserted, DepositRecordOutcome.Recorded
          │
          └─ ✓ Recorded 1 new deposit
          
Time: T3  (T2 + ConfirmationInterval = +10 sec)
          Confirmation worker runs:
          │
          ├─ GetTrackableAsync(Tron) → [Deposit #789]
          ├─ GetTipHeight() → 12346 (one block later)
          ├─ GetBlockAsync(12345) → BlockRef(hash)
          │  └─ Still canonical
          │
          ├─ confirmations = 12346 - 12345 + 1 = 2
          ├─ isFinalized = false (12345 < finalizedHeight 12340? No)
          │
          ├─ deposit.RegisterConfirmations(2, false, policy)
          │  └─ policy.RequiredConfirmations = 30
          │  └─ 2 >= 30? No → stay Detected
          │
          └─ No state change
          
Time: T4-T31
          (Confirmation loop runs every 10 sec)
          │
          ├─ T11: confirmations=12, not yet creditable
          ├─ T21: confirmations=22, not yet creditable
          └─ T31: confirmations=32, policy.IsCreditable(32, false) = true!
          
Time: T32
          Confirmation worker runs:
          │
          ├─ GetTipHeight() → 12375
          ├─ GetBlockAsync(12345) → still canonical
          ├─ confirmations = 12375 - 12345 + 1 = 31
          │
          ├─ deposit.RegisterConfirmations(31, false, policy)
          │  ├─ 31 >= 30? YES
          │  ├─ Status = Confirmed
          │  ├─ ConfirmedAt = now
          │  │
          │  └─ Raise DepositConfirmed event
          │     └─ Write to Outbox in same transaction
          │
          ├─ Repository.SaveChangesAsync()
          │  └─ Single transaction:
          │     ├─ UPDATE Deposit SET Status=2, ...
          │     └─ INSERT OutboxMessage (DepositConfirmed)
          │
          └─ ✓ 1 deposit confirmed
          
Time: T33
          OutboxDispatcher runs:
          │
          ├─ Acquire Redis lock (distributed)
          ├─ SELECT unprocessed from deposit.OutboxMessage
          │  └─ Found: DepositConfirmed
          │
          ├─ Deserialize → DepositConfirmed event
          ├─ IEventBus.PublishAsync(event)
          │  │
          │  └─ DepositConfirmedHandler.HandleAsync()
          │     │
          │     ├─ Parse event.AmountBaseUnits → BigInteger
          │     ├─ ILedgerPoster.CreditDepositAsync(...)
          │     │  │
          │     │  ├─ Create/get MerchantLiabilityAccount(merchant, asset)
          │     │  ├─ Create/get PlatformClearingAccount(asset)
          │     │  │
          │     │  ├─ Journal.Post(
          │     │  │    ReferenceType="Deposit", ReferenceId=depositId,
          │     │  │    lines=[
          │     │  │      Debit(PlatformClearing, 1000000),
          │     │  │      Credit(MerchantLiability, 1000000)
          │     │  │    ]
          │     │  │  )
          │     │  │  └─ Validate balanced
          │     │  │
          │     │  ├─ INSERT Journal + JournalEntry
          │     │  ├─ UPDATE AccountBalance
          │     │  │
          │     │  └─ UNIQUE(ReferenceType, ReferenceId)
          │     │     = deduplication (safe redelivery)
          │     │
          │     └─ ✓ Ledger credited
          │
          ├─ Mark message Processed = true
          ├─ Commit
          │
          └─ Release Redis lock
          
Time: T34+
          User checks balance:
          │
          ├─ GET /merchants/{merchantId}/balance?assetId=...
          ├─ ILedgerQuery.GetMerchantBalanceAsync(merchantId, assetId)
          │  │
          │  └─ SELECT AccountBalance.Balance
          │     WHERE AccountType=MerchantLiability
          │       AND OwnerId=merchantId
          │       AND AssetId=assetId
          │
          └─ Response: 1000000 (1 USDT)
          
✓ DEPOSIT FLOW COMPLETE
```

---

## 11. Key Architectural Properties

### Idempotency

| Layer | Mechanism |
|-------|-----------|
| **Deposit Detection** | `(Chain, TxHash, OutputIndex)` UNIQUE dedup index |
| **Deposit State Confirmation** | Idempotent state transitions (e.g., Detected→Confirmed only once) |
| **Ledger Posting** | `(ReferenceType, ReferenceId)` UNIQUE dedup index |
| **Outbox Delivery** | Event marked Processed after handler succeeds; retry if fails |

### Consistency

| Boundary | Guarantee |
|----------|-----------|
| **Single Deposit Transaction** | Atomicity: aggregate mutation + outbox event write in one commit |
| **Ledger Journal** | All entries balanced before commit; no partial postings |
| **Double-Entry** | Platform clearing always equals sum of merchant liabilities |
| **Reorg Handling** | Compensating entries posted if deposit orphaned after credit |

### Durability

| Concern | Design |
|---------|--------|
| **Chain Scan Progress** | Cursor persisted after window processed (crash re-scans, never skips) |
| **Deposit State** | Immutable historic record; only status and confirmations mutable |
| **Events** | Stored in Outbox before delivery; undelivered events retry forever |
| **Ledger** | Append-only journals; never edit history, only post compensating entries |

### Resiliency

| Failure Mode | Behavior |
|--------------|----------|
| **Scanner crashes mid-window** | Cursor not advanced; next pass re-scans same block range |
| **Chain unavailable** | Worker logs error, retries on next tick |
| **Confirmation worker crashes** | Unconfirmed deposits tracked from scratch on recovery |
| **Reorg detected** | DepositOrphaned event raises; ledger reverses automatically |
| **Ledger handler fails** | Event stays in outbox; dispatcher retries until success |
| **Outbox dispatcher crashes** | Unprocessed events retry on recovery; Redis lock prevents dups |

---

## 12. Money Safety Rules

### Amount Handling

```csharp
// Amounts are ALWAYS unsigned BigInteger (base units)
public BigInteger Amount { get; }     // wei, sun, lamports

// Never:
double amount;                         // ✗ Precision loss
decimal amount;                        // ✗ Scaled value (1.23 USD)

// For transport (JSON):
string AmountBaseUnits;                // ✓ Lossless "1000000"

// At UI boundary only:
// Convert via Asset.Decimals
// 1000000 sun / 10^6 = 1.000000 USDT (display)
```

### Immutability

```
Deposit.Amount        → Immutable (never changes)
Deposit.Status        → Mutable (state machine)
Deposit.BlockHash     → Mutable (can change in reorg)
Journal Entries       → Immutable (append-only)
AccountBalance        → Derived (rebuilt from journal)
```

### No Silent Losses

```
✗ Rounding: Amount capped at 38 decimals
  Larger amounts rejected at ingestion

✗ Truncation: DECIMAL(38,0) never silently truncates
  NonPositiveAmount or OverflowException

✗ Double-spending: Dust is tagged Ignored
  Never credited, auditable

✗ Reorg loss: Compensating entries ensure
  Net balance recovered after reorg
```

---

## 13. Testing Strategy

**Unit Tests:**
- `DepositDomainTests` — State transitions, validations
- `JournalDomainTests` — Balance equations
- `AccountBalanceTests` — Normal sides, credit/debit

**Integration Tests:**
- `DepositPersistenceTests` — DB dedup, cursor
- `DepositToLedgerTests` — End-to-end (scanner → confirmation → ledger)
- `LedgerPostingTests` — Journal balancing, idempotency

**Test Fixtures:**
- `DepositTestHost` — Real SQL Server, in-memory chain source
- `InMemoryChainSource` — Deterministic block simulation (AddBlock, ReplaceBlock)
- `FakeWalletDirectory` — Configurable address ownership

All test paths exercise the real persistence layer (SQL Server Testcontainers).
