# Deposit Flow — Visual Diagrams & Mermaid Charts

## 1. Module Interaction Diagram

```mermaid
graph TB
    User["User sends funds"]
    Chain["Blockchain (TRON)"]
    
    Scanner["DepositScannerWorker<br/>(Periodic 10s)"]
    Confirm["DepositConfirmationWorker<br/>(Periodic 10s)"]
    Dispatcher["OutboxDispatcher<br/>(Periodic 5s)"]
    
    Deposit["Deposit Module<br/>(Detection/Confirmation)"]
    Wallet["Wallet Module<br/>(Address provisioning)"]
    Ledger["Ledger Module<br/>(Double-entry posting)"]
    
    User -->|sends USDT| Chain
    Chain -->|RPC calls| Scanner
    Scanner -->|scan window| Deposit
    
    Deposit -->|trackable deposits| Confirm
    Confirm -->|check confirmations| Chain
    Confirm -->|raises events| Deposit
    
    Deposit -->|outbox messages| Dispatcher
    Dispatcher -->|publishes events| Ledger
    Ledger -->|posts journal| Ledger
    
    Wallet -->|provisions address| Deposit
    Deposit -->|uses address| Wallet
```

---

## 2. Deposit State Machine (Detailed)

```mermaid
stateDiagram-v2
    [*] --> DETECTED: Deposit.Record()<br/>amount >= minDeposit
    [*] --> IGNORED: Deposit.Record()<br/>amount < minDeposit
    
    IGNORED --> [*]: Never credited<br/>(audit only)
    
    DETECTED --> DETECTED: RegisterConfirmations()<br/>confirmations < required
    DETECTED --> DETECTED: Reorg detected<br/>but block still canonical
    
    DETECTED --> CONFIRMED: RegisterConfirmations()<br/>confirmations >= required<br/>OR isFinalized
    DETECTED --> ORPHANED: MarkOrphaned()<br/>Block gone or hash changed<br/>No ledger impact
    
    CONFIRMED --> ORPHANED: MarkOrphaned()<br/>Block reorg'd away<br/>Raises DepositOrphaned event
    
    CONFIRMED --> [*]: Confirmed forever<br/>(if no reorg)
    ORPHANED --> [*]: Orphaned forever
    
    note right of DETECTED
        • Waiting for confirmations
        • Scan cursor resumable
        • No ledger entries yet
        • Idempotent state transitions
    end note
    
    note right of CONFIRMED
        • Policy threshold met
        • DepositConfirmed event raised
        • Outbox → Ledger → credit posted
        • Merchant balance updated
    end note
    
    note right of ORPHANED
        • Block no longer canonical
        • If was CONFIRMED:
          DepositOrphaned event raised
          Ledger posts compensating entry
          Merchant balance reversed
        • If was DETECTED:
          Silent orphan (no ledger impact)
    end note
```

---

## 3. Wallet Provisioning Flow

```mermaid
sequenceDiagram
    participant User
    participant API
    participant WalletProvisioner as WalletProvisioningService
    participant MerchantDir as IMerchantDirectory
    participant KeyMgmt as IWalletDerivation<br/>(KeyManagement)
    participant WalletRepo as IWalletRepository
    participant DB as SQL Server<br/>(Wallet schema)
    
    User->>API: POST /merchants/{id}/deposit-addresses<br/>{chain: "Tron"}
    
    API->>WalletProvisioner: ProvisionDepositAddressAsync(merchantId, chain)
    
    WalletProvisioner->>MerchantDir: FindByIdAsync(merchantId)
    MerchantDir->>DB: SELECT * FROM Merchant WHERE Id=?
    DB-->>MerchantDir: MerchantSummary
    MerchantDir-->>WalletProvisioner: merchant (verified CanTransact=true)
    
    WalletProvisioner->>KeyMgmt: AllocateNextAsync(chain, DerivationPurpose.Deposit)
    Note over KeyMgmt: HD Wallet derivation<br/>(secp256k1 for TRON)<br/>BIP-32 index allocation
    KeyMgmt->>DB: [KeyManagement schema]<br/>Allocate index atomically
    DB-->>KeyMgmt: DerivedAddress<br/>(DerivedKeyId, Address)
    KeyMgmt-->>WalletProvisioner: derived
    
    WalletProvisioner->>WalletProvisioner: Wallet.CreateDeposit()<br/>Validate: keyId, chain, address, merchantId
    WalletProvisioner->>WalletProvisioner: Seed WalletAssignment<br/>(merchant → wallet link)
    
    WalletProvisioner->>WalletRepo: Add(wallet)
    WalletProvisioner->>WalletRepo: SaveChangesAsync()
    
    WalletRepo->>DB: BEGIN TRANSACTION
    WalletRepo->>DB: INSERT Wallet (id, chain, address, ...)<br/>INSERT WalletAssignment (...)
    DB-->>WalletRepo: ✓ Committed
    WalletRepo-->>WalletProvisioner: ✓ Success
    
    WalletProvisioner-->>API: ProvisionedDepositAddress<br/>{walletId, address, chain}
    API-->>User: 200 OK<br/>{address: "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t"}<br/>(now watched by scanner)
```

---

## 4. Deposit Detection & Scanning

```mermaid
sequenceDiagram
    participant Worker as DepositScannerWorker<br/>(10s interval)
    participant Detection as DepositDetectionService
    participant Scanner as IDepositScanner<br/>(InMemoryChainSource)
    participant WalletDir as IWalletDirectory
    participant DepositDomain as Deposit.Record()
    participant Repo as IDepositRepository
    participant DB as SQL Server<br/>(Deposit schema)
    
    Worker->>Worker: PeriodicTimer tick (every 10s)
    
    Worker->>Detection: ScanOnceAsync(Chain.Tron)
    
    Detection->>Scanner: GetTipHeightAsync()
    Scanner-->>Detection: tipHeight: 12350
    
    Detection->>DB: SELECT LastScannedBlock FROM ScanCursor
    DB-->>Detection: lastScanned: 0
    
    Note over Detection: Calculate window<br/>fromBlock = 0 + 1 = 1<br/>toBlock = min(12350, 1 + 500 - 1) = 500
    
    Detection->>Scanner: ScanAsync(1, 500)
    Note over Scanner: Scan InMemoryChainSource<br/>blocks 1-500<br/>for all transfers
    Scanner-->>Detection: [DetectedTransfer, ...]<br/>(chain, address, amount, tx, blockNumber)
    
    loop For each DetectedTransfer
        Detection->>WalletDir: FindByAddressAsync(chain, address)
        
        alt Address is ours
            WalletDir-->>Detection: WalletOwnership(walletId, merchantId, ...)
            
            Detection->>Detection: Check wallet active, has merchant, is Deposit type
            
            Detection->>DepositDomain: Deposit.Record(chain, address, walletId, merchantId, assetId, amount, ...)<br/>+ DepositPolicy
            
            Note over DepositDomain: Validate: amount > 0, address/tx hash required, owners not empty<br/>Status = (amount >= minDeposit) ? Detected : Ignored
            
            DepositDomain-->>Detection: Result<Deposit>
            
            Detection->>Repo: AddIfNewAsync(deposit)
            
            Repo->>DB: INSERT Deposit (...)<br/>UNIQUE(Chain, TxHash, OutputIndex)
            
            alt First time
                DB-->>Repo: ✓ Inserted
                Repo-->>Detection: DepositRecordOutcome.Recorded
                Detection->>Detection: recorded++
            else Already exists (re-scan)
                DB-->>Repo: ✗ UNIQUE violation (2601)
                Repo-->>Detection: DepositRecordOutcome.Duplicate
                Note over Detection: Idempotent: ignore duplicate
            end
        else Address not ours
            WalletDir-->>Detection: null
            Note over Detection: Skip (not our address)
        end
    end
    
    Detection->>DB: UPDATE ScanCursor SET LastScannedBlock=500
    
    Detection-->>Worker: recorded count
    
    Note over Worker: Log if recorded > 0<br/>Wait 10 seconds<br/>Repeat
```

---

## 5. Deposit Confirmation & Reorg Detection

```mermaid
sequenceDiagram
    participant Worker as DepositConfirmationWorker<br/>(10s interval)
    participant Confirm as DepositConfirmationService
    participant Repo as IDepositRepository
    participant ChainStatus as IChainStatusReader
    participant Domain as Deposit aggregate
    participant EventBus as Outbox (DomainEvent)
    participant DB as SQL Server
    
    Worker->>Worker: PeriodicTimer tick (every 10s)
    
    Worker->>Confirm: TrackOnceAsync(Chain.Tron)
    
    Confirm->>Repo: GetTrackableAsync(Chain.Tron)
    Note over Repo: SELECT * FROM Deposit<br/>WHERE Chain = Tron<br/>AND Status IN (Detected, Confirmed)
    DB-->>Repo: [Deposit #1 (Detected, 2 conf), Deposit #2 (Confirmed, 35 conf), ...]
    Repo-->>Confirm: tracked deposits
    
    Confirm->>ChainStatus: GetTipHeightAsync()
    ChainStatus-->>Confirm: tipHeight: 12365
    
    Confirm->>ChainStatus: GetFinalizedHeightAsync()
    ChainStatus-->>Confirm: finalizedHeight: 12340
    
    Confirm->>Confirm: Load policy (RequiredConfirmations = 30)
    
    loop For each tracked Deposit
        Confirm->>ChainStatus: GetBlockAsync(chain, blockNumber)<br/>e.g., GetBlockAsync(Tron, 12335)
        
        alt Block found and hash matches
            ChainStatus-->>Confirm: BlockRef(blockNumber, blockHash)
            Note over Confirm: Block still canonical
            
            Confirm->>Confirm: confirmations = tipHeight - blockNumber + 1<br/>             = 12365 - 12335 + 1 = 31<br/>isFinalized = blockNumber <= finalizedHeight<br/>            = 12335 <= 12340? true
            
            Confirm->>Domain: RegisterConfirmations(31, true, policy, now)
            
            alt Policy creditable
                Note over Domain: isCreditable(31, true)<br/>= (31 >= 30) OR true<br/>= true
                Domain->>Domain: Status = Confirmed<br/>ConfirmedAt = now
                Domain->>Domain: Raise DepositConfirmed event<br/>(EventId, DepositId, MerchantId, AssetId, Amount, ...)
                Domain-->>Confirm: ✓
                Confirm->>Confirm: changed++
            else Not yet creditable
                Note over Domain: isCreditable(5, false)<br/>= 5 >= 30 = false<br/>Keep Detected
                Domain-->>Confirm: (no change)
            end
        
        else Block not found OR hash mismatch
            ChainStatus-->>Confirm: null OR BlockRef(newHash)
            Note over Confirm: REORG DETECTED!<br/>Block no longer canonical
            
            Confirm->>Domain: MarkOrphaned(now)
            
            Domain->>Domain: Status = Orphaned<br/>UpdatedAt = now
            
            alt Was Confirmed before
                Note over Domain: wasCredited = true
                Domain->>Domain: Raise DepositOrphaned event<br/>(EventId, DepositId, MerchantId, AssetId, Amount, ...)
                Domain-->>Confirm: ✓
                Note over Confirm: Ledger will post<br/>compensating entry
            else Was Detected (never confirmed)
                Note over Domain: wasCredited = false<br/>Silent orphan<br/>(no ledger impact)
                Domain-->>Confirm: ✓ (no event)
            end
            
            Confirm->>Confirm: changed++
        end
    end
    
    Confirm->>Repo: SaveChangesAsync()
    
    Repo->>DB: BEGIN TRANSACTION<br/>UPDATE Deposit SET Status=?, Confirmations=?, ... (all mutations)<br/>INSERT OutboxMessage (DepositConfirmed, DepositOrphaned)<br/>(all events raised by aggregates)
    
    EventBus->>EventBus: Collect domain events from aggregates<br/>(via IHasDomainEvents)
    EventBus->>DB: Write to Outbox table in same transaction
    
    DB-->>Repo: ✓ COMMIT (all-or-nothing)
    
    Repo-->>Confirm: changed count
    Confirm-->>Worker: changed count
    
    Note over Worker: Log if changed > 0<br/>Wait 10 seconds<br/>Repeat
```

---

## 6. Outbox Dispatch & Ledger Integration

```mermaid
sequenceDiagram
    participant Dispatcher as OutboxDispatcher<br/>(5s interval)
    participant Redis as Redis<br/>(Distributed Lock)
    participant DB as SQL Server<br/>(Outbox table)
    participant EventBus as IEventBus<br/>(in-process)
    participant Handler as DepositConfirmedHandler
    participant LedgerPoster as ILedgerPoster
    participant LedgerDB as SQL Server<br/>(Ledger schema)
    
    Dispatcher->>Dispatcher: PeriodicTimer tick (every 5s)
    
    Dispatcher->>Redis: SET key:{module}:lock NX EX 30<br/>(acquire distributed lock, TTL 30s)
    
    alt Lock acquired
        Redis-->>Dispatcher: ✓ Got lock
        
        Dispatcher->>DB: SELECT * FROM deposit.OutboxMessage<br/>WHERE Processed = 0<br/>ORDER BY CreatedAt
        DB-->>Dispatcher: [OutboxMessage(Id, Type='DepositConfirmed', Payload=JSON)]
        
        loop For each unprocessed message
            Dispatcher->>Dispatcher: Deserialize JSON → DepositConfirmed event
            
            Dispatcher->>EventBus: PublishAsync(event)
            
            EventBus->>Handler: HandleAsync(DepositConfirmed)<br/>event = {DepositId, MerchantId, AssetId, AmountBaseUnits='1000000', ...}
            
            Handler->>Handler: amount = BigInteger.Parse('1000000')
            Handler->>LedgerPoster: CreditDepositAsync(new CreditDepositCommand(...)
            
            LedgerPoster->>LedgerDB: SELECT Account WHERE<br/>AccountType=MerchantLiability AND OwnerId=merchantId AND AssetId=assetId
            
            alt Account exists
                LedgerDB-->>LedgerPoster: Account
            else Account not found
                LedgerPoster->>LedgerDB: Account.Open(MerchantLiability, Merchant, ...)<br/>Account.Open(PlatformClearing, Platform, ...)
                LedgerDB-->>LedgerPoster: ✓ Created
            end
            
            LedgerPoster->>LedgerPoster: Journal.Post(<br/>  ReferenceType='Deposit', ReferenceId=depositId,<br/>  lines=[<br/>    Debit(PlatformClearing, 1000000),<br/>    Credit(MerchantLiability, 1000000)<br/>  ]<br/>)
            
            Note over LedgerPoster: Validate balanced:<br/>totalDebit == totalCredit
            
            LedgerPoster->>LedgerDB: BEGIN TRANSACTION<br/>INSERT Journal (...)<br/>INSERT JournalEntry (...)<br/>INSERT JournalEntry (...)<br/>UPDATE AccountBalance SET Balance = Balance ± amount<br/>UNIQUE(ReferenceType, ReferenceId) on Journal
            
            alt Success
                LedgerDB-->>LedgerPoster: ✓ COMMIT
                LedgerPoster-->>Handler: PostingOutcome.Posted
                Handler-->>EventBus: ✓
                EventBus-->>Dispatcher: ✓ Handler complete
            else Failure (e.g., corrupt amount)
                LedgerDB-->>LedgerPoster: ✗ ROLLBACK
                LedgerPoster-->>Handler: Result.Failure(...)
                Handler-->>EventBus: ✗ Throw DomainException
                EventBus-->>Dispatcher: ✗ Exception propagated
                Note over Dispatcher: Handler failed:<br/>Leave message unprocessed<br/>Retry on next tick
            end
            
            alt Handler succeeded
                Dispatcher->>DB: UPDATE deposit.OutboxMessage<br/>SET Processed=1, ProcessedAt=now<br/>WHERE Id=messageId
                DB-->>Dispatcher: ✓ Marked processed
            else Handler failed
                Note over Dispatcher: Message stays unprocessed<br/>Retry after 5 seconds
            end
        end
        
        Dispatcher->>Redis: DEL key:{module}:lock<br/>(release lock)
        Redis-->>Dispatcher: ✓
    else Lock not acquired
        Redis-->>Dispatcher: ✗ Already locked<br/>(another instance processing)
        Note over Dispatcher: Skip this tick<br/>Retry in 5 seconds
    end
```

---

## 7. Complete Happy Path: Address → Deposit → Confirmation → Credit

```mermaid
timeline
    title Deposit Flow (Happy Path) - Timeline View
    
    section Merchant Setup
    T0 : Merchant requests deposit address
        : WalletProvisioner.ProvisionDepositAddressAsync()
        : Creates Wallet + Derivation
        : Returns address: TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t
    
    section User Sends Funds
    T0+ : User sends 1 USDT to address
        : Transfer in block 12345 on TRON
    
    section Scanner (Every 10s)
    T10 : DepositScannerWorker tick
        : DepositDetectionService.ScanOnceAsync()
        : FindByAddressAsync() → found wallet
        : Deposit.Record() → Status=Detected
        : AddIfNewAsync() → Recorded
        : ✓ 1 new deposit detected
    
    T10+ : Worker waits 10 seconds
    
    section Confirmation Worker (Every 10s)
    T20 : DepositConfirmationWorker tick
        : GetTrackableAsync() → [Deposit]
        : GetTipHeight() → 12346
        : confirmations = 12346 - 12345 + 1 = 2
        : policy.IsCreditable(2, false)? No
        : Status stays Detected
    
    T30 : DepositConfirmationWorker tick
        : confirmations = 3
        : Still < required 30
    
    ... : (skip intermediate ticks)
    
    T320 : DepositConfirmationWorker tick
        : confirmations = 31
        : policy.IsCreditable(31, false) = true!
        : Status = Confirmed, raise DepositConfirmed
        : SaveChangesAsync() → commit with Outbox
        : ✓ Deposit confirmed, event in Outbox
    
    section Outbox Dispatcher (Every 5s)
    T325 : OutboxDispatcher tick
        : SELECT unprocessed messages
        : DepositConfirmedHandler.HandleAsync()
        : ILedgerPoster.CreditDepositAsync()
        : Journal.Post() with balanced entries
        : INSERT Journal + JournalEntries
        : UPDATE AccountBalance
        : UPDATE OutboxMessage SET Processed=1
        : ✓ Ledger credited, message marked processed
    
    section Balance Check
    T330+ : User/API queries balance
        : ILedgerQuery.GetMerchantBalanceAsync()
        : Returns 1000000 (1 USDT in base units)
        : ✓ Merchant balance updated!
```

---

## 8. Reorg Scenario: Deposit Orphaned After Confirmation

```mermaid
timeline
    title Reorg Detection & Ledger Reversal
    
    section Normal Confirmation
    T320 : Deposit confirmed at block 12345
        : DepositConfirmed event raised
        : Outbox → Ledger credit posted
        : Merchant balance = 1 USDT
    
    section Reorg Happens On-Chain
    T400 : REORG!
        : Block 12345 replaced with different transactions
        : New block hash ≠ old hash
        : Canonical chain reorganized
    
    section Reorg Detection
    T410 : DepositConfirmationWorker tick
        : GetTrackableAsync() → [Deposit (Confirmed)]
        : GetBlockAsync(12345) → BlockRef(NEW_HASH)
        : new hash != stored hash
        : ⚠️ REORG DETECTED!
        : deposit.MarkOrphaned()
        : Status = Orphaned
        : wasCredited = true
        : Raise DepositOrphaned event
        : SaveChangesAsync() → commit with Outbox
        : ✓ Orphan event in Outbox
    
    section Ledger Reversal
    T415 : OutboxDispatcher tick
        : DepositOrphanedHandler.HandleAsync()
        : ILedgerPoster.ReverseDepositAsync()
        : Journal.Post(compensating entries)
        : Original credit: Debit Platform, Credit Merchant
        : Compensating: Credit Platform, Debit Merchant
        : Net effect = 0
        : ✓ Ledger balanced, merchant balance = 0
    
    section Verification
    T420+ : User queries balance
        : Returns 0 USDT
        : Ledger now has both entries for audit
        : ✓ Money not lost, fully reversible
```

---

## 9. Data Flow (Block Diagram)

```mermaid
graph LR
    subgraph Blockchain["Blockchain (TRON)"]
        Block["Block 12345<br/>TxHash: 0xabc..."]
        Transfer["Transfer<br/>to TR7NHqje...<br/>1 USDT"]
    end
    
    subgraph Deposit["Deposit Module"]
        Scanner["IDepositScanner<br/>(scan 1-500)"]
        Detection["DepositDetectionService<br/>(scan loop)"]
        Deposit_["Deposit aggregate<br/>(Status=Detected)"]
        DepositDB["DepositDbContext<br/>Deposits table"]
        Cursor["ScanCursor<br/>(LastScannedBlock=500)"]
    end
    
    subgraph Wallet["Wallet Module"]
        WalletDir["IWalletDirectory<br/>(find by address)"]
        WalletDB["WalletDbContext<br/>Wallets table"]
    end
    
    subgraph Confirmation["Confirmation & Events"]
        Confirm["DepositConfirmationService<br/>(track once)"]
        ChainStatus["IChainStatusReader<br/>(check block)"]
        Deposit_Confirmed["DepositConfirmed event<br/>(raised by aggregate)"]
        Outbox["OutboxMessage<br/>(durable queue)"]
    end
    
    subgraph Ledger["Ledger Module"]
        Handler["DepositConfirmedHandler<br/>(event consumer)"]
        Poster["ILedgerPoster<br/>(credit deposit)"]
        Journal["Journal<br/>(balanced entries)"]
        JournalEntry["JournalEntry<br/>(debit/credit lines)"]
        AccountBalance["AccountBalance<br/>(merchant balance cache)"]
        LedgerDB["LedgerDbContext<br/>Ledger schema"]
    end
    
    Block -->|Transfer found| Scanner
    Transfer -->|address, amount| Scanner
    Scanner -->|DetectedTransfer| Detection
    Detection -->|FindByAddressAsync| WalletDir
    WalletDir -->|WalletOwnership| Detection
    WalletDB -->|Wallet with address| WalletDir
    Detection -->|Record + AddIfNewAsync| Deposit_
    Deposit_ -->|INSERT + UNIQUE dedup| DepositDB
    DepositDB -->|Deposits table| Cursor
    
    DepositDB -->|GetTrackableAsync| Confirm
    ChainStatus -->|GetTipHeight, GetBlockAsync| Confirm
    Confirm -->|RegisterConfirmations| Deposit_
    Deposit_ -->|Aggregate raises| Deposit_Confirmed
    Deposit_Confirmed -->|Write to Outbox| Outbox
    DepositDB -->|same transaction| Outbox
    
    Outbox -->|Dispatch| Handler
    Handler -->|Parse event| Handler
    Handler -->|CreditDepositAsync| Poster
    Poster -->|Journal.Post| Journal
    Journal -->|Create entries| JournalEntry
    JournalEntry -->|Debit + Credit| Journal
    Journal -->|INSERT| LedgerDB
    JournalEntry -->|INSERT| LedgerDB
    AccountBalance -->|UPDATE Balance| LedgerDB
    
    style Blockchain fill:#e1f5ff
    style Deposit fill:#f3e5f5
    style Wallet fill:#fff3e0
    style Confirmation fill:#e8f5e9
    style Ledger fill:#fce4ec
```

---

## 10. Concurrency & Locks

```mermaid
graph TB
    subgraph Worker["Worker Loop"]
        ScannerWorker["DepositScannerWorker<br/>Thread: BackgroundService"]
        ConfirmationWorker["DepositConfirmationWorker<br/>Thread: BackgroundService"]
        OutboxDispatcher["OutboxDispatcher<br/>Thread: BackgroundService"]
    end
    
    subgraph Synchronization["Synchronization Points"]
        DbLock["SQL Server<br/>Locking & Transactions"]
        RedisLock["Redis<br/>Distributed Lock<br/>(key: outbox:{module})<br/>TTL: 30s"]
    end
    
    subgraph State["Shared State"]
        DepositDB["DepositDbContext<br/>(Deposits, ScanCursor, Outbox)"]
        LedgerDB["LedgerDbContext<br/>(Journals, Accounts, Balances)"]
        Chain["InMemoryChainSource<br/>(thread-safe: lock _gate)"]
    end
    
    ScannerWorker -->|Single-writer<br/>per chain| DbLock
    ConfirmationWorker -->|Single-writer<br/>per chain| DbLock
    
    ConfirmationWorker -->|WriteOutboxMessages| DepositDB
    OutboxDispatcher -->|ReadOutboxMessages<br/>AcquireLock| RedisLock
    
    OutboxDispatcher -->|ReadOutboxMessages| DepositDB
    OutboxDispatcher -->|WriteJournals| LedgerDB
    
    DbLock -->|SERIALIZABLE<br/>or SNAPSHOT<br/>isolation| DepositDB
    DbLock -->|SERIALIZABLE<br/>isolation| LedgerDB
    
    ScannerWorker -->|ConcurrentRead| Chain
    ConfirmationWorker -->|ConcurrentRead| Chain
    
    style Worker fill:#e3f2fd
    style Synchronization fill:#fff9c4
    style State fill:#f1f8e9
```
