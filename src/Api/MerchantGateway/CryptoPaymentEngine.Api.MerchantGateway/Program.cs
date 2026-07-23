using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;
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
// signature middleware enforces, since Swashbuckle can't see them on its own (§7.1). One registration —
// this used to be two separate AddSwaggerGen calls that both registered a "v1" doc, which throws
// ArgumentException("An item with the same key has already been added: v1") the instant Swashbuckle's
// options are actually resolved; merged into one so that's no longer possible.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Crypto.Gateway.Core — Merchant API",
        Version = "v1",
        Description =
            "Merchant-facing API. Requests to /api/v1/* are HMAC-signed: X-Api-Key + X-Timestamp + " +
            "X-Signature = HMAC-SHA256(hexDecode(signingSecret), \"{timestamp}\\n{body}\").\n\n" +
            "In Development, 'Try it out' signs each call for you with the dev seed merchant's key — leave the " +
            "three X- headers blank and just Execute. (tools/dev/Invoke-MerchantRequest.ps1 does the same from " +
            "PowerShell.) A real merchant integration must compute the signature itself.\n\n" +
            "Deposit flow: POST /api/v1/deposit -> open the returned payUrl -> send USDT to the address shown -> " +
            "it is detected, credited to the ledger (net + fee split), matched to the invoice, and the signed " +
            "merchant callback fires (watch it at GET /dev/callbacks). The pay page and /dev/* are unauthenticated.",
    });
    o.OperationFilter<CryptoPaymentEngine.Api.MerchantGateway.Security.HmacHeadersOperationFilter>();
});

// ── Business modules (each owns one capability; the host only composes — §4.7) ──
builder.Services.AddMerchantModule(config, dbConnection);
builder.Services.AddKeyManagementModule(dbConnection);
builder.Services.AddBlockchainAddressEncoding();
builder.Services.AddWalletModule(dbConnection);
builder.Services.AddEnergyModule(config, dbConnection);  // TRON resource monitoring (Phase 5a): SQL policy + Mongo snapshots
builder.Services.AddLedgerModule(dbConnection);         // consumes Deposit + Withdrawal events (credit/settle/release)
builder.Services.AddDepositModule(config, dbConnection);
builder.Services.AddWithdrawalModule(config, dbConnection);
builder.Services.AddConfigurationAssetCatalog();        // canonical AssetId shared by edge, scanner, ledger
builder.Services.AddPaymentIntentModule(config, dbConnection); // deposit invoices + address pool; matches DepositConfirmed
builder.Services.AddNotificationModule();               // consumes PaymentIntentMatched → signed merchant callback

// ── Chain source + signing: swap dev↔prod by DI, not code (§8, §10) ───────────
if (builder.Environment.IsDevelopment())
{
    // Live money-out on TRON Nile testnet (Level 3): opt in with Withdrawal:LiveTron=true plus a THROWAWAY
    // testnet key + endpoint in the git-ignored appsettings.Local.json (see docs/withdrawal-testnet.md). It
    // implies the real chain source too, because confirming a broadcast withdrawal needs the real tip/finality.
    var liveTron = config.GetValue<bool>("Withdrawal:LiveTron");

    // Deposits: real mainnet/testnet detection when opted in (Chains:Tron:Live, or implied by LiveTron, + a
    // fresh TronGrid/Nile key in appsettings.Local.json), else the deterministic node-free in-memory source.
    if (config.GetValue<bool>("Chains:Tron:Live") || liveTron)
    {
        builder.Services.AddJsonRpcChainSources();
        builder.Services.AddTronChainAdapter(config);
    }
    else
    {
        builder.Services.AddInMemoryChainSource();
    }

    if (liveTron)
    {
        // Real TRON build → broadcast → status over the node, and a real secp256k1 signer over the throwaway
        // testnet key. The key never leaves the signer (§10); this branch is Development-only and gated, so a
        // real key-holding signer can never be wired in production.
        builder.Services.AddTronTransactionEngine(config);
        builder.Services.AddTronSigner();
    }
    else
    {
        builder.Services.AddInMemoryTransactionEngine();
        builder.Services.AddInMemorySigner(); // NEVER touches a key; a real KMS signer replaces it in prod (§10)
    }

    builder.Services.AddInMemoryAccountResourceReader(); // read-only resource observation for the Energy monitor

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
    // Also NOT built: the real TRON getaccountresource adapter (IAccountResourceReader) — the Energy monitor
    // stays inert in prod until it lands (deferred to staging like the other JSON-RPC adapters, §8).
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

// TRON resource monitor (Phase 5a): samples every platform wallet's energy, snapshots to Mongo, alerts on
// Low/Critical. Read-only — no money, no keys. Inert until an IAccountResourceReader is registered (dev: in-memory).
builder.Services.AddEnergyWorkers(new EnergyWorkerOptions
{
    Chains = [Chain.Tron],
    MonitorInterval = TimeSpan.FromSeconds(30),
});

// Relays each module's outbox → IEventBus → the Ledger handlers: Deposit credit, Withdrawal
// settle/release. This is what makes the money-in and money-out paths live end to end.
builder.Services.AddOutboxDispatcher<DepositDbContext>();
builder.Services.AddOutboxDispatcher<WithdrawalDbContext>();
builder.Services.AddOutboxDispatcher<PaymentIntentDbContext>(); // relays PaymentIntentMatched → the callback handler

// NOTE: In Development, ISecretProvider is the in-memory provider wired by AddDevelopmentKeyCustody above
// (public xpub only). In production it is NOT registered yet — a real KMS-backed ISecretProvider + prod
// HD-wallet rows must be supplied before /deposit can provision an address there (never an in-memory seed, §10).

var app = builder.Build();

// Dev-only Swagger UI at /swagger (developer testing). Absent in production.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Merchant Gateway v1");
        c.RoutePrefix = "swagger";

        // Sign /api/v1 calls in the browser with the dev seed merchant's key, so "Try it out" exercises the
        // REAL signed flow rather than 400ing on the missing signature. Dev-only, and only when the dev
        // merchant seed is on — a real signing secret must never be embedded in a page (§10).
        var devApiKey = config["Merchant:DevSeed:ApiKey"];
        var devSigningSecret = config["Merchant:DevSeed:SigningSecret"];
        if (config.GetValue<bool>("Merchant:DevSeed:Enabled")
            && !string.IsNullOrWhiteSpace(devApiKey)
            && !string.IsNullOrWhiteSpace(devSigningSecret))
        {
            c.UseRequestInterceptor(DevSwaggerRequestSigning.InterceptorJs(devApiKey, devSigningSecret));
        }
    });
}

// Serves the hosted pay page (wwwroot/pay.html) and any static assets. Unauthenticated by design.
app.UseStaticFiles();

// The frozen merchant API is authenticated by request signature; the pay-page data + health pass through.
app.UseMiddleware<MerchantSignatureMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapMerchantApi();   // POST /api/v1/{deposit,withdraw,balance}
app.MapPayApi();        // GET  /pay/{ref}  (page) + /pay/{ref}/info (data)

// Dev-only: the in-host callback sink so a human can watch the merchant callback fire on deposit detection.
if (app.Environment.IsDevelopment())
    app.MapDevEndpoints();

app.Run();
