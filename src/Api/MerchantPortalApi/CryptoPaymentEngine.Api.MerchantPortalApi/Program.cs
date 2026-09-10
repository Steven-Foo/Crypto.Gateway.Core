using CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure;
using CryptoPaymentEngine.Infrastructure.Locking;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Per-developer overrides (git-ignored) — same convention as the other hosts.
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

builder.Services.AddRedisInfrastructure(redisConnection); // needed by PaymentIntent's wallet reservation lock

// httpOnly session-cookie settings (name / SameSite / Secure), same as the Ops host.
builder.Services.Configure<MerchantSessionCookieOptions>(config.GetSection(MerchantSessionCookieOptions.SectionName));

// Credentialed CORS for the browser portal — an explicit allow-list of exact origins (never '*'), or the
// browser will neither send nor accept the session cookie. Empty ⇒ same-origin only (safe default).
var corsOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (corsOrigins.Length == 0)
        return;
    policy.WithOrigins(corsOrigins).AllowCredentials().AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Crypto.Gateway.Core — Merchant Portal API", Version = "v1" }));

// ── Business modules this host composes, all TENANT-SCOPED (§4.7 — a host is composition only, and this one
// registers no workers/dispatchers, so it never scans/processes/broadcasts money-moving background work).
// MerchantIdentity is the portal's own auth. Reads dominate, but Phase 2 adds writes that call the SAME
// Application services the HMAC MerchantGateway uses (withdrawal request / merchant cash-out / credential
// rotation) — the service layer is the money boundary, not the host, so every existing control still applies.
// The in-memory chain/signer below exist only to satisfy Withdrawal's HotWalletAllocator DI graph: this host
// never signs or broadcasts (the money host's workers do).
builder.Services.AddMerchantIdentityModule(config, dbConnection);

// Merchant-admin action logging. The SAME store the Back Office writes to — entries are separated by the
// tenant stamped on each row, not by living in different tables, so one query vocabulary covers both
// audiences and a merchant can never read a staff entry (see AuditScope).
builder.Services.AddAuditModule(dbConnection);
builder.Services.AddMerchantModule(config, dbConnection);
builder.Services.AddKeyManagementModule(dbConnection);
builder.Services.AddBlockchainAddressEncoding();
builder.Services.AddConfigurationAssetCatalog();
builder.Services.AddWalletModule(dbConnection);
builder.Services.AddPaymentIntentModule(config, dbConnection);
builder.Services.AddDepositModule(config, dbConnection);      // read-only: IDepositLookup for payin enrichment
builder.Services.AddWithdrawalModule(config, dbConnection);   // IWithdrawalDirectory (read) + request/cash-out services (write)
builder.Services.AddTreasuryModule(dbConnection);             // needed by Withdrawal's HotWalletAllocator
builder.Services.AddLedgerModule(dbConnection);               // read-only: ILedgerQuery for funds

if (builder.Environment.IsDevelopment())
{
    // Public xpub only, never a seed (§10) — must point at the SAME HD wallet as the other hosts.
    builder.Services.AddDevelopmentKeyCustody(config);

    // Seed the dev merchant (idempotent, same config as the other hosts) so the portal login can bind to it,
    // then seed the merchant-portal login itself.
    builder.Services.AddDevelopmentMerchantSeed(config);
    builder.Services.AddDevelopmentMerchantPortalSeed(config);

    // This host never calls a chain adapter or signer itself, but AddWithdrawalModule/AddTreasuryModule wire up
    // HotWalletAllocator/etc. which need IBalanceReader/ITransactionBuilder/ISigner to construct — so the
    // Development-only DI validation needs SOMETHING registered. These never touch a real key or chain (§10).
    builder.Services.AddInMemoryChainSource();
    builder.Services.AddInMemoryTransactionEngine();
    builder.Services.AddInMemorySigner();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Merchant Portal API v1"));
}

// CORS before auth so preflight (OPTIONS) and 401/403 responses carry CORS headers.
app.UseCors();

app.UseMiddleware<MerchantSessionAuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPortalAuthApi();
app.MapPortalProfileApi();
app.MapPortalFundsApi();
app.MapPortalFeeApi();
app.MapPortalAddressApi();
app.MapPortalTransactionApi();
app.MapPortalAccountApi();
app.MapPortalCredentialApi();
app.MapPortalMoneyOutApi();
app.MapPortalTopUpApi();
app.MapPortalActivityApi();

app.Run();
