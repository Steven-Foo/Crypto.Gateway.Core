using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Workers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Workers;
using CryptoPaymentEngine.Infrastructure.Events;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Outbox;
using CryptoPaymentEngine.SharedKernel;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var config = builder.Configuration;
var dbConnection = config["Db:ConnectionString"]
    ?? throw new InvalidOperationException("Missing configuration 'Db:ConnectionString'.");
var redisConnection = config["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing configuration 'Redis:ConnectionString'.");

// ── Cross-cutting infrastructure ──────────────────────────────────────────────
builder.Services.AddCryptoPaymentEngineEventBus();
builder.Services.AddRedisInfrastructure(redisConnection);

// ── Business modules (each owns one capability; the host only composes — §4.7) ──
builder.Services.AddMerchantModule(config, dbConnection);
builder.Services.AddKeyManagementModule(dbConnection);
builder.Services.AddBlockchainAddressEncoding();
builder.Services.AddWalletModule(dbConnection);
builder.Services.AddLedgerModule(dbConnection);         // consumes Deposit + Withdrawal events (credit/settle/release)
builder.Services.AddDepositModule(config, dbConnection);
builder.Services.AddWithdrawalModule(config, dbConnection);

// ── Chain source + signing: swap dev↔prod by DI, not code (§8, §10) ───────────
if (builder.Environment.IsDevelopment())
{
    // Deterministic, node-free — drive from a test/seed.
    builder.Services.AddInMemoryChainSource();
    builder.Services.AddInMemoryTransactionEngine();
    builder.Services.AddInMemorySigner(); // NEVER touches a key; a real KMS signer replaces it in prod (§10)
}
else
{
    builder.Services.AddJsonRpcChainSources();
    builder.Services.AddTronChainAdapter(config);
    // NOT built yet: real per-chain ITransactionBuilder/ITransactionBroadcaster and the KMS-backed ISigner.
    // Withdrawal processing stays inert in prod until these land — by design, never a fake signer.
}

// ── Background processing ─────────────────────────────────────────────────────
builder.Services.AddDepositWorkers(new DepositWorkerOptions
{
    Chains = [Chain.Tron],
    ScanInterval = TimeSpan.FromSeconds(10),
    ConfirmationInterval = TimeSpan.FromSeconds(10),
});

builder.Services.AddWithdrawalWorkers(new WithdrawalWorkerOptions
{
    ProcessInterval = TimeSpan.FromSeconds(10),
    ConfirmationInterval = TimeSpan.FromSeconds(10),
});

// Relays each module's outbox → IEventBus → the Ledger handlers: Deposit credit, Withdrawal
// settle/release. This is what makes the money-in and money-out paths live end to end.
builder.Services.AddOutboxDispatcher<DepositDbContext>();
builder.Services.AddOutboxDispatcher<WithdrawalDbContext>();

// NOTE: ISecretProvider (KMS/HSM) is intentionally NOT registered here yet — HD-wallet derivation
// (address provisioning) needs it, and it must be a real KMS in prod, never an in-memory dev seed.
// The money-in flow being wired above does not require it.

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
