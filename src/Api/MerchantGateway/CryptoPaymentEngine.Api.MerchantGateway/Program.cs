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
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Workers;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure;
using CryptoPaymentEngine.Api.MerchantGateway.Endpoints;
using CryptoPaymentEngine.Api.MerchantGateway.Security;
using CryptoPaymentEngine.Infrastructure.Events;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Outbox;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Per-developer overrides (git-ignored). Highest precedence in a local run — a developer drops any value
// here (e.g. a real branch xpub under KeyManagement:DevSecrets) without touching committed config. Absent
// in production, where the file does not exist.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

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

// Dev-only interactive API docs — UI is gated behind Development below (never in prod, matching the
// Swagger-gating fix already applied to APIGateway's hosts). Documents the HMAC headers the merchant
// signature middleware enforces, since Swashbuckle can't see them on its own (§7.1).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Crypto.Gateway.Core — Merchant API", Version = "v1" });
    o.OperationFilter<CryptoPaymentEngine.Api.MerchantGateway.Security.HmacHeadersOperationFilter>();
});

// ── Business modules (each owns one capability; the host only composes — §4.7) ──
builder.Services.AddMerchantModule(config, dbConnection);
builder.Services.AddKeyManagementModule(dbConnection);
builder.Services.AddBlockchainAddressEncoding();
builder.Services.AddWalletModule(dbConnection);
builder.Services.AddLedgerModule(dbConnection);         // consumes Deposit + Withdrawal events (credit/settle/release)
builder.Services.AddDepositModule(config, dbConnection);
builder.Services.AddWithdrawalModule(config, dbConnection);
builder.Services.AddConfigurationAssetCatalog();        // canonical AssetId shared by edge, scanner, ledger
builder.Services.AddPaymentIntentModule(config, dbConnection); // deposit invoices + address pool; matches DepositConfirmed
builder.Services.AddNotificationModule();               // consumes PaymentIntentMatched → signed merchant callback

// ── Chain source + signing: swap dev↔prod by DI, not code (§8, §10) ───────────
if (builder.Environment.IsDevelopment())
{
    // Chain source: the deterministic in-memory fake by default (node-free, safe on a fresh clone), or the
    // REAL adapter when a developer explicitly opts in (Chains:UseLiveNode=true in appsettings.Local.json)
    // to run a live round-trip against a real testnet. Custody/signing stay fake either way (§10) — this
    // flag only ever affects which chain the scanner watches, never what's allowed to hold or move a key.
    if (config.GetValue<bool>("Chains:UseLiveNode"))
    {
        builder.Services.AddJsonRpcChainSources();
        builder.Services.AddTronChainAdapter(config);
    }
    else
    {
        builder.Services.AddInMemoryChainSource();
    }

    builder.Services.AddInMemoryTransactionEngine();
    builder.Services.AddInMemorySigner(); // NEVER touches a key; a real KMS signer replaces it in prod (§10)

    // In-memory secret provider (PUBLIC xpub only) + idempotent HD-wallet seeder, so a signed /deposit can
    // provision an address on a fresh clone. Overridable per-developer via appsettings.Local.json. The
    // provider is InMemoryDevelopment-kind, so it can never back a production wallet row (§10).
    builder.Services.AddDevelopmentKeyCustody(config);

    // A fixed, documented test merchant (active) so a signed /api/v1 request works out of the box — the last
    // piece for a full deposit round-trip in dev. Config in Merchant:DevSeed; never runs in production (§10).
    builder.Services.AddDevelopmentMerchantSeed(config);
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

// Frees lapsed deposit-invoice addresses back to the pool (§9).
builder.Services.AddPaymentIntentWorkers();

// Relays each module's outbox → IEventBus → the Ledger handlers: Deposit credit, Withdrawal
// settle/release. This is what makes the money-in and money-out paths live end to end.
builder.Services.AddOutboxDispatcher<DepositDbContext>();
builder.Services.AddOutboxDispatcher<WithdrawalDbContext>();
builder.Services.AddOutboxDispatcher<PaymentIntentDbContext>(); // relays PaymentIntentMatched → the callback handler

// NOTE: In Development, ISecretProvider is the in-memory provider wired by AddDevelopmentKeyCustody above
// (public xpub only). In production it is NOT registered yet — a real KMS-backed ISecretProvider + prod
// HD-wallet rows must be supplied before /deposit can provision an address there (never an in-memory seed, §10).

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Merchant API v1"));
}

// The frozen merchant API is authenticated by request signature; the pay-page data + health pass through.
app.UseMiddleware<MerchantSignatureMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapMerchantApi();   // POST /api/v1/{deposit,withdraw,balance}
app.MapPayApi();        // GET  /pay/{ref}/info

app.Run();
