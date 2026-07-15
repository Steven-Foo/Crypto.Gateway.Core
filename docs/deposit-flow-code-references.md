# Deposit Flow — Code References & Testing

## 1. File Structure & Key Classes

### Domain Layer (Business Rules)

```
src/Gateway.Core/PaymentProcessing/Deposit/Domain/
├── Deposit.cs                    # Aggregate root
│   ├─ Record()                   # Factory: detect → create
│   ├─ RegisterConfirmations()    # Advance confirmations
│   ├─ MarkOrphaned()             # Reorg detection
│   └─ RaiseEvent()               # Domain event emission
│
├── DepositStatus.cs              # Enum: Detected, Confirmed, Orphaned, Ignored
├── DepositPolicy.cs              # Per-chain policy (MinDeposit, RequiredConfirmations)
├── CreditStrategy.cs             # Enum: Confirmations vs. Finalized
└── DepositErrors.cs              # Domain error constants
```

### Application Layer (Use Cases)

```
src/Gateway.Core/PaymentProcessing/Deposit/Application/
├── DepositDetectionService.cs
│   └─ ScanOnceAsync(chain)       # Scan blocks, detect transfers, record deposits
│
├── DepositConfirmationService.cs
│   └─ TrackOnceAsync(chain)      # Track pending deposits, detect reorgs, raise events
│
└── Abstractions/
    ├── IDepositRepository.cs      # Aggregate persistence
    ├── IDepositPolicyProvider.cs  # Policy lookup
    └── IScanCursorStore.cs        # Cursor persistence
```

### Infrastructure Layer (EF Core, Persistence)

```
src/Gateway.Core/PaymentProcessing/Deposit/Infrastructure/
├── Persistence/
│   ├── DepositDbContext.cs       # Module's DbContext + Outbox
│   ├── DepositRepository.cs      # IDepositRepository impl
│   ├── ScanCursorStore.cs        # IScanCursorStore impl
│   ├── DepositMap.cs             # EF Core fluent config
│   │   └─ UNIQUE(Chain, TxHash, OutputIndex)
│   │
│   └── Migrations/
│       └── 20260713082649_InitialDeposit.cs
│
└── DepositModuleExtensions.cs    # DI registration
```

### Events (Published)

```
src/Gateway.Core/PaymentProcessing/Deposit/Events/
├── DepositConfirmed.cs           # Raised when policy threshold met
│   └─ Consumed by: LedgerModule (credit)
│
└── DepositOrphaned.cs            # Raised when reorg detected (if was confirmed)
    └─ Consumed by: LedgerModule (reverse)
```

### Workers (Background Processing)

```
src/Gateway.Core/PaymentProcessing/Deposit/Workers/
├── DepositScannerWorker.cs
│   └─ BackgroundService (periodic scan)
│
├── DepositConfirmationWorker.cs
│   └─ BackgroundService (periodic confirmation)
│
├── DepositWorkersExtensions.cs   # DI registration
└── DepositWorkerOptions.cs       # Configuration options
```

### Tests

```
src/Gateway.Core/PaymentProcessing/Deposit/Tests/
├── DepositTestHost.cs            # Base fixture (SQL Server + in-memory chain)
├── DepositDomainTests.cs         # State transitions, validations
├── DepositPersistenceTests.cs    # DB dedup, cursor persistence
├── DepositToLedgerTests.cs       # End-to-end (scanner → ledger)
└── OutboxDispatcherTests.cs      # Event dispatch durability
```

---

## 2. Key Dependencies & Interfaces

### From Blockchain Module

```csharp
// IDepositScanner — scan chain for transfers
public interface IDepositScanner
{
    Task<IReadOnlyList<DetectedTransfer>> ScanAsync(
        Chain chain, long fromBlock, long toBlock, CancellationToken ct);
}
// Impl: InMemoryChainSource (dev) or TronChainAdapter (prod)

// IChainStatusReader — check block status & reorg detection
public interface IChainStatusReader
{
    Task<long> GetTipHeightAsync(Chain chain, CancellationToken ct);
    Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken ct);
    Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken ct);
}
// Impl: InMemoryChainSource (dev) or RoutingChainSource (prod)
```

### From Wallet Module

```csharp
// IWalletDirectory — identify address ownership
public interface IWalletDirectory
{
    Task<WalletOwnership?> FindByAddressAsync(
        Chain chain, string address, CancellationToken ct);
}
// Record: WalletOwnership(WalletId, MerchantId, Chain, Address, IsActive, WalletType)

// IDepositAddressProvisioner — create new addresses
public interface IDepositAddressProvisioner
{
    Task<Result<ProvisionedDepositAddress>> ProvisionDepositAddressAsync(
        Guid merchantId, Chain chain, CancellationToken ct);
}
```

### From Merchant Module

```csharp
// IMerchantDirectory — verify merchant exists & can transact
public interface IMerchantDirectory
{
    Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken ct);
}
// Record: MerchantSummary(MerchantId, MerchantCode, Name, CanTransact)
```

### From KeyManagement Module

```csharp
// IWalletDerivation — derive HD addresses (no key exposure)
public interface IWalletDerivation
{
    Task<Result<DerivedAddress>> AllocateNextAsync(
        Chain chain, DerivationPurpose purpose, CancellationToken ct);
}
// Record: DerivedAddress(DerivedKeyId, Chain, Address, DerivationIndex, DerivationPath)
```

### From Ledger Module

```csharp
// ILedgerPoster — post double-entry journals
public interface ILedgerPoster
{
    Task<Result<PostingOutcome>> CreditDepositAsync(
        CreditDepositCommand cmd, CancellationToken ct);
    
    Task<Result<PostingOutcome>> ReverseDepositAsync(
        ReverseDepositCommand cmd, CancellationToken ct);
}

// ILedgerQuery — read merchant balance
public interface ILedgerQuery
{
    Task<BigInteger> GetMerchantBalanceAsync(
        Guid merchantId, Guid assetId, CancellationToken ct);
}
```

---

## 3. Configuration (appsettings.json)

```json
{
  "Deposit": {
    "Policies": {
      "Tron": {
        "CreditStrategy": "Confirmations",     // or "Finalized"
        "RequiredConfirmations": 30,
        "MinDeposit": "1000000"                // 1 USDT (6 decimals) in sun
      }
    }
  },
  "DepositWorkerOptions": {
    "Chains": [ "Tron" ],
    "ScanInterval": "00:00:10",                // 10 seconds
    "ConfirmationInterval": "00:00:10"
  },
  "Chains": {
    "Tron": {
      "RpcUrl": "https://api.trongrid.io",
      "ApiKey": "...",
      "Confirmations": 30
    }
  }
}
```

---

## 4. Program.cs Registration

```csharp
// In MerchantGateway/Program.cs

var builder = WebApplication.CreateBuilder(args);

// ── Business modules ──
builder.Services.AddDepositModule(config, dbConnection);

// ── Background workers ──
builder.Services.AddDepositWorkers(new DepositWorkerOptions
{
    Chains = [Chain.Tron],
    ScanInterval = TimeSpan.FromSeconds(10),
    ConfirmationInterval = TimeSpan.FromSeconds(10),
});

// ── Event dispatch (durability) ──
builder.Services.AddOutboxDispatcher<DepositDbContext>();

// ── In-memory chain source (dev) or JSON-RPC (prod) ──
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddInMemoryChainSource();
}
else
{
    builder.Services.AddJsonRpcChainSources();
    builder.Services.AddTronChainAdapter(config);
}

var app = builder.Build();
app.Run();
```

---

## 5. Testing Strategy

### Unit Tests: Domain Logic

**File:** `DepositDomainTests.cs`

```csharp
// Example test
[Fact]
public void Record_WithAmountBelowMinimum_CreatesIgnoredDeposit()
{
    var policy = new DepositPolicy(
        CreditStrategy.Confirmations,
        requiredConfirmations: 30,
        minDeposit: BigInteger.Parse("1000000"));  // 1 USDT in sun
    
    var result = Deposit.Record(
        chain: Chain.Tron,
        address: "TR7NHqje...",
        walletId: Guid.NewGuid(),
        merchantId: Guid.NewGuid(),
        assetId: Guid.NewGuid(),
        amount: BigInteger.Parse("100000"),  // Only 0.1 USDT (below dust)
        transactionHash: "0xabc123...",
        outputIndex: 0,
        blockNumber: 12345,
        blockHash: "0xdef456...",
        policy: policy,
        now: DateTimeOffset.UtcNow);
    
    Assert.True(result.IsSuccess);
    Assert.Equal(DepositStatus.Ignored, result.Value.Status);
}

// Test state transitions
[Fact]
public void RegisterConfirmations_AtPolicyThreshold_RaisesConfirmedEvent()
{
    var deposit = Deposit.Record(..., policy).Value;
    
    deposit.RegisterConfirmations(30, false, policy, now);
    
    Assert.Equal(DepositStatus.Confirmed, deposit.Status);
    Assert.NotNull(deposit.ConfirmedAt);
    Assert.Single(deposit.GetDomainEvents());  // DepositConfirmed
}

// Test reorg handling
[Fact]
public void MarkOrphaned_AfterConfirmation_RaisesOrphanedEvent()
{
    var deposit = Deposit.Record(..., policy).Value;
    deposit.RegisterConfirmations(31, false, policy, now);
    
    deposit.MarkOrphaned(now);
    
    Assert.Equal(DepositStatus.Orphaned, deposit.Status);
    var events = deposit.GetDomainEvents();
    Assert.Contains(events, e => e is DepositOrphaned);
}
```

### Integration Tests: Persistence & Deduplication

**File:** `DepositPersistenceTests.cs`

```csharp
[Fact]
public async Task AddIfNew_WithDuplicateKey_ReturnsDuplicate()
{
    var context = new DepositDbContext(...);
    var repository = new DepositRepository(context);
    
    var deposit1 = Deposit.Record(...).Value;
    var deposit2 = Deposit.Record(
        transactionHash: deposit1.TransactionHash,
        outputIndex: deposit1.OutputIndex,
        ...
    ).Value;
    
    var outcome1 = await repository.AddIfNewAsync(deposit1);
    Assert.Equal(DepositRecordOutcome.Recorded, outcome1);
    
    var outcome2 = await repository.AddIfNewAsync(deposit2);
    Assert.Equal(DepositRecordOutcome.Duplicate, outcome2);  // Dedup worked!
}

[Fact]
public async Task ScanCursor_AfterScan_PersistsLastBlockNumber()
{
    var cursor = new ScanCursorStore(context, TimeProvider.System);
    
    await cursor.SetLastScannedBlockAsync(Chain.Tron, 500);
    
    var saved = await cursor.GetLastScannedBlockAsync(Chain.Tron);
    Assert.Equal(500, saved);
}
```

### Integration Tests: End-to-End (Scanner → Ledger)

**File:** `DepositToLedgerTests.cs`

```csharp
[Fact]
public async Task ScannerDetectsTransfer_ConfirmationCreditsLedger_BalanceUpdated()
{
    // Setup: Create merchant, wallet, address
    var merchantId = Guid.NewGuid();
    var walletId = Guid.NewGuid();
    var assetId = Guid.NewGuid();
    var address = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
    
    var walletOwnership = new WalletOwnership(walletId, merchantId, Chain.Tron, address, true, "Deposit");
    var wallets = new FakeWalletDirectory().Register(walletOwnership);
    
    var policy = new DepositPolicy(CreditStrategy.Confirmations, 30, BigInteger.Parse("1000000"));
    
    // Step 1: Simulate on-chain transfer
    var chain = new InMemoryChainSource();
    chain.AddBlock(Chain.Tron, 12345, "0xblock1", new DetectedTransfer(
        Chain.Tron, address, assetId, BigInteger.Parse("1000000"),
        "0xtx1", 0, 12345, "0xblock1"));
    chain.SetFinalizedHeight(Chain.Tron, 12340);
    
    // Step 2: Run scanner
    var depositDetection = new DepositDetectionService(
        chain, chain, wallets, depositRepository, cursorStore, 
        new StubPolicyProvider(policy), TimeProvider.System, logger);
    
    var recorded = await depositDetection.ScanOnceAsync(Chain.Tron);
    Assert.Equal(1, recorded);
    Assert.Single(await depositRepository.GetTrackableAsync(Chain.Tron));
    
    // Step 3: Run confirmation (not yet at policy threshold)
    var depositConfirmation = new DepositConfirmationService(
        chain, depositRepository, new StubPolicyProvider(policy),
        TimeProvider.System, logger);
    
    var changed = await depositConfirmation.TrackOnceAsync(Chain.Tron);
    Assert.Equal(0, changed);  // Not enough confirmations yet
    
    // Step 4: Add more blocks
    for (int i = 12346; i <= 12375; i++)
    {
        chain.AddBlock(Chain.Tron, i, $"0xblock{i}", /* no transfers */);
    }
    
    // Step 5: Run confirmation again (now at 31 confirmations)
    changed = await depositConfirmation.TrackOnceAsync(Chain.Tron);
    Assert.Equal(1, changed);  // Deposit advanced to Confirmed
    
    var deposit = (await depositRepository.GetTrackableAsync(Chain.Tron)).First();
    Assert.Equal(DepositStatus.Confirmed, deposit.Status);
    
    // Step 6: Dispatch outbox events
    var outboxMessages = context.OutboxMessages
        .Where(m => !m.Processed)
        .ToList();
    
    Assert.Single(outboxMessages);  // DepositConfirmed
    
    var @event = JsonSerializer.Deserialize<DepositConfirmed>(outboxMessages.First().Payload);
    
    var ledgerPoster = new LedgerPoster(ledgerContext, ledgerAccountStore, ledgerPostingStore);
    var result = await ledgerPoster.CreditDepositAsync(
        new CreditDepositCommand(@event.DepositId, @event.MerchantId, @event.AssetId, 
            BigInteger.Parse(@event.AmountBaseUnits)));
    
    Assert.True(result.IsSuccess);
    Assert.Equal(PostingOutcome.Posted, result.Value);
    
    // Step 7: Verify balance
    var ledgerQuery = new LedgerQuery(ledgerContext);
    var balance = await ledgerQuery.GetMerchantBalanceAsync(merchantId, assetId);
    Assert.Equal(BigInteger.Parse("1000000"), balance);
}
```

### Test Fixture: DepositTestHost

```csharp
public abstract class DepositTestHost : IAsyncLifetime
{
    // Real SQL Server (via LocalDB or Testcontainers)
    protected static DepositDbContext Context() =>
        new(new DbContextOptionsBuilder<DepositDbContext>()
            .UseSqlServer(ConnectionString)
            .UseBigIntegerMoney()
            .Options);
    
    // In-memory chain (deterministic, no node)
    protected static InMemoryChainSource Chain() =>
        new();
    
    // Service instances
    protected static DepositDetectionService Detection(...) =>
        new(chain, chain, wallets, repository, cursors, policies, 
            TimeProvider.System, logger);
    
    protected static DepositConfirmationService Confirmation(...) =>
        new(chain, repository, policies, TimeProvider.System, logger);
    
    public async ValueTask InitializeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }
    
    public async ValueTask DisposeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync();
    }
}
```

### Test Helpers

```csharp
protected sealed class FakeWalletDirectory : IWalletDirectory
{
    private readonly Dictionary<(Chain, string), WalletOwnership> _byAddress = [];
    
    public FakeWalletDirectory Register(WalletOwnership ownership)
    {
        _byAddress[(ownership.Chain, ownership.Address)] = ownership;
        return this;
    }
    
    public Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken ct = default) =>
        Task.FromResult(_byAddress.GetValueOrDefault((chain, address)));
}

protected sealed class StubPolicyProvider(DepositPolicy policy) : IDepositPolicyProvider
{
    public DepositPolicy For(Chain chain) => policy;
}
```

---

## 6. Execution Flow (Code Path Reference)

```
1. USER PROVISIONS ADDRESS
   POST /api/merchants/{merchantId}/deposit-addresses
   └─ WalletProvisioningService.ProvisionDepositAddressAsync()
      ├─ IMerchantDirectory.FindByIdAsync() (Contracts read)
      ├─ IWalletDerivation.AllocateNextAsync() (KeyManagement)
      ├─ Wallet.CreateDeposit() (Domain)
      └─ IWalletRepository.SaveChangesAsync()

2. USER SENDS FUNDS ON-CHAIN
   Chain includes transfer to address TR7NHqje...

3. SCANNER WORKER (Background, periodic)
   DepositScannerWorker.ExecuteAsync() ─ PeriodicTimer loop
   └─ DepositDetectionService.ScanOnceAsync(Chain.Tron)
      ├─ IDepositScanner.ScanAsync() (InMemoryChainSource)
      ├─ IChainStatusReader.GetTipHeightAsync()
      ├─ IScanCursorStore.GetLastScannedBlockAsync()
      ├─ For each DetectedTransfer:
      │  ├─ IWalletDirectory.FindByAddressAsync() (Contracts read)
      │  ├─ Deposit.Record(..., policy) (Domain)
      │  └─ IDepositRepository.AddIfNewAsync() (Persist with dedup)
      └─ IScanCursorStore.SetLastScannedBlockAsync() (Cursor advance)

4. CONFIRMATION WORKER (Background, periodic)
   DepositConfirmationWorker.ExecuteAsync() ─ PeriodicTimer loop
   └─ DepositConfirmationService.TrackOnceAsync(Chain.Tron)
      ├─ IDepositRepository.GetTrackableAsync()
      ├─ IChainStatusReader.GetTipHeightAsync()
      ├─ IChainStatusReader.GetFinalizedHeightAsync()
      ├─ IDepositPolicyProvider.For(chain)
      ├─ For each tracked Deposit:
      │  ├─ IChainStatusReader.GetBlockAsync() (Reorg check)
      │  ├─ Deposit.RegisterConfirmations() (Advance state)
      │  │  └─ If policy.IsCreditable(): Raise DepositConfirmed
      │  │     └─ Aggregate.Raise() (DomainEvent)
      │  └─ OR Deposit.MarkOrphaned()
      │     └─ If was Confirmed: Raise DepositOrphaned
      └─ IDepositRepository.SaveChangesAsync()
         └─ EF Core captures domain events
            └─ Outbox writes events to OutboxMessage table

5. OUTBOX DISPATCHER (Background, periodic)
   OutboxDispatcher<DepositDbContext>.ExecuteAsync() ─ PeriodicTimer loop
   ├─ Redis distributed lock (single-flight)
   └─ While messages unprocessed:
      ├─ SELECT from OutboxMessage WHERE Processed=0
      ├─ Deserialize JSON → DepositConfirmed or DepositOrphaned
      ├─ IEventBus.PublishAsync(event)
      │  └─ DepositConfirmedHandler.HandleAsync() OR
      │     DepositOrphanedHandler.HandleAsync()
      │     └─ ILedgerPoster.CreditDepositAsync() OR
      │        ILedgerPoster.ReverseDepositAsync()
      │        └─ Journal.Post() (balanced double-entry)
      │           └─ LedgerDbContext.SaveChangesAsync()
      └─ UPDATE OutboxMessage SET Processed=1
         └─ If handler fails: Leave unprocessed for retry

6. USER CHECKS BALANCE
   GET /api/merchants/{merchantId}/balance?assetId=...
   └─ ILedgerQuery.GetMerchantBalanceAsync(merchantId, assetId)
      └─ SELECT AccountBalance WHERE MerchantLiability = merchantId
         └─ Return balance in base units
```

---

## 7. Error Handling & Validation

### Domain Validation (Aggregate Factory)

```csharp
// Deposit.Record() validates:
if (string.IsNullOrWhiteSpace(address))
    return Result.Failure<Deposit>(DepositErrors.AddressRequired);

if (string.IsNullOrWhiteSpace(transactionHash))
    return Result.Failure<Deposit>(DepositErrors.TransactionHashRequired);

if (amount <= BigInteger.Zero)
    return Result.Failure<Deposit>(DepositErrors.AmountNotPositive);

if (walletId == Guid.Empty || merchantId == Guid.Empty || assetId == Guid.Empty)
    return Result.Failure<Deposit>(DepositErrors.OwnerRequired);

// Status assigned based on policy
var status = policy.MeetsMinimum(amount) 
    ? DepositStatus.Detected 
    : DepositStatus.Ignored;
```

### Application Validation

```csharp
// DepositDetectionService filters:
if (owner is null || !owner.IsActive || owner.MerchantId is null 
    || owner.WalletType != "Deposit")
    continue;  // Skip (not our address, inactive, no merchant, wrong type)

// DepositConfirmationService detects reorg:
if (canonical is null || !string.Equals(canonical.BlockHash, deposit.BlockHash, ...))
{
    deposit.MarkOrphaned(now);  // Idempotent
}
```

### Persistence Errors

```csharp
// DepositRepository catches duplicate constraint:
catch (DbUpdateException ex) when (IsDedupViolation(ex))
{
    context.Entry(deposit).State = EntityState.Detached;
    return DepositRecordOutcome.Duplicate;
}

// Duplicate is treated as success (idempotent)
// No exception thrown; caller ignores
```

### Event Handler Errors

```csharp
// DepositConfirmedHandler catches ledger errors:
if (result.IsFailure)
    throw new DomainException(
        $"Ledger credit failed for deposit {event.DepositId}: {result.Error!.Code}");

// Handler failure leaves message in Outbox unprocessed
// OutboxDispatcher retries on next tick
// Message never lost
```

---

## 8. Performance & Scalability Considerations

### Indexing Strategy

```sql
-- Deposit scans by address (wallet lookup)
CREATE INDEX IX_Deposit_Address ON deposit.Deposit(Address);

-- Confirmation tracking by chain + status
CREATE INDEX IX_Deposit_Chain_Status ON deposit.Deposit(Chain, Status);

-- FK lookups
CREATE INDEX IX_Deposit_WalletId ON deposit.Deposit(WalletId);
CREATE INDEX IX_Deposit_MerchantId ON deposit.Deposit(MerchantId);

-- Outbox queries
CREATE INDEX IX_OutboxMessage_Type_Processed 
    ON deposit.OutboxMessage(Type, Processed, Id);
```

### Cursor Management

```
ScanCursor prevents re-scanning blocks:
• After processing blocks 1-500, cursor = 500
• Crash before cursor advance? Blocks 1-500 re-scanned (idempotent)
• No blocks skipped, no gaps in coverage
```

### Single-Flight Dispatch

```
Redis distributed lock prevents duplicate ledger posts:
• Only one OutboxDispatcher processes messages per module
• If dispatcher crashes, next instance acquires lock after TTL expires
• Ledger's (ReferenceType, ReferenceId) UNIQUE deduplicates anyway
```

### Idempotency Chain

```
Level 1: Deposit detection
└─ (Chain, TxHash, OutputIndex) UNIQUE prevents duplicate recording

Level 2: Ledger posting
└─ (ReferenceType, ReferenceId) UNIQUE prevents duplicate journals

Level 3: Event delivery
└─ OutboxMessage marked Processed after handler succeeds
```

---

## 9. Monitoring & Observability

### Logging Points

```csharp
// Scanner
logger.LogInformation("Recorded {Count} new deposit(s) on {Chain}.", recorded, chain);

// Confirmation
logger.LogInformation("{Count} deposit(s) changed state on {Chain}.", changed, chain);
logger.LogWarning("Deposit {DepositId} orphaned AFTER confirmation on {Chain}", depositId, chain);

// Event handler failure
logger.LogError(ex, "Deposit scan failed for {Chain}", chain);
```

### Metrics to Track

1. **Deposits Detected** — gauge (deposits in Detected status)
2. **Deposits Confirmed** — counter (cumulative confirmations)
3. **Deposits Orphaned** — counter (reorgs detected)
4. **Confirmation Latency** — histogram (blocks to policy threshold)
5. **Outbox Lag** — gauge (unprocessed messages)
6. **Ledger Posts** — counter (successful journals written)

### Audit Trail

```
Ledger journal = immutable audit record
├─ Original credit: Debit Platform, Credit Merchant
├─ Reorg reversal: Credit Platform, Debit Merchant
└─ Both entries visible; net effect audit-trail
```
