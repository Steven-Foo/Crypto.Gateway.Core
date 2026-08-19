using CryptoPaymentEngine.Api.OperationsApi.Endpoints;
using CryptoPaymentEngine.Api.OperationsApi.Options;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Api.OperationsApi.Services;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure;
using CryptoPaymentEngine.Infrastructure.Locking;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Per-developer overrides (git-ignored) — same convention as MerchantGateway.
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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Crypto.Gateway.Core — Operations API", Version = "v1" }));

// Cloudflare IP-allowlist sync — disabled (no-op) until a real ApiToken/ZoneId is configured (§ ops).
builder.Services.Configure<CloudflareOptions>(config.GetSection("Cloudflare"));
builder.Services.AddHttpClient<CloudflareService>(c =>
{
    c.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
    var token = config["Cloudflare:ApiToken"];
    if (!string.IsNullOrEmpty(token))
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
});

// ── Business modules this host composes — merchant/custody setup, staff identity, read-only ledger
// history, plus enough of PaymentIntent/Deposit/Withdrawal/Notification for the Ops transaction-search
// screens. Deposit/Withdrawal/Notification are composed for their read Contracts ONLY (IDepositLookup,
// IWithdrawalDirectory.SearchAsync, ICallbackDeliveryQuery) — this host registers no workers/dispatchers for
// them, so it never scans/processes/broadcasts/dispatches money-moving background work (§4.7 — a host is
// composition only, no business logic of its own). Ledger is likewise read-only (ILedgerQuery only).
// PaymentIntent never touches the ledger either way (§ PaymentIntent design) — a manual fail is only
// reachable pre-match, before any deposit has been credited.
builder.Services.AddMerchantModule(config, dbConnection);
builder.Services.AddKeyManagementModule(dbConnection);
builder.Services.AddBlockchainAddressEncoding();
builder.Services.AddConfigurationAssetCatalog();
builder.Services.AddWalletModule(dbConnection);
builder.Services.AddPaymentIntentModule(config, dbConnection);
builder.Services.AddDepositModule(config, dbConnection);       // read-only use here: IDepositLookup for /transactions/deposits
// Withdrawal's DI registration wires up HotWalletAllocator unconditionally (it doesn't know a given host is
// read-only), and HotWalletAllocator needs ITreasuryHotWalletDirectory — so Treasury must be composed
// alongside Withdrawal even though this host only ever calls IWithdrawalDirectory.SearchAsync. Without this,
// ASP.NET Core's Development-only service validation fails at boot (no implementation registered for
// ITreasuryHotWalletDirectory), before the host ever starts listening.
builder.Services.AddTreasuryModule(dbConnection);
builder.Services.AddWithdrawalModule(config, dbConnection);    // read-only use here: IWithdrawalDirectory for /transactions/withdrawals
builder.Services.AddNotificationModule(dbConnection);          // read-only use here: ICallbackDeliveryQuery for both transaction screens
builder.Services.AddLedgerModule(dbConnection); // read-only use here: ILedgerQuery for /transactions
builder.Services.AddIdentityModule(config, dbConnection); // staff login/logout/session validation
builder.Services.AddAuditModule(dbConnection); // staff-action logging, called directly by mutating Ops endpoints

if (builder.Environment.IsDevelopment())
{
    // Public xpub only, never a seed (§10) — same dev-only seam MerchantGateway uses, and must point at
    // the SAME HD wallet (matching config) so addresses derived here are consistent with ones derived there.
    builder.Services.AddDevelopmentKeyCustody(config);

    // Fixed Admin credentials so a fresh clone can call /api/v1/ops/auth/login with no bootstrap step.
    builder.Services.AddDevelopmentStaffSeed(config);

    // This host never calls a chain adapter or a signer itself (no build/sign/broadcast endpoint exists
    // here) — but AddTreasuryModule/AddWithdrawalModule unconditionally wire up HotWalletAllocator/
    // TreasuryReloadService, which need IBalanceReader/ITransactionBuilder/ISigner to construct at all, so
    // ASP.NET Core's Development-only service validation fails at boot without SOMETHING registered for
    // them. These never touch a real key or a real chain (§10) — purely to satisfy the DI graph.
    // KNOWN GAP: there is no Staging/Production branch for these yet in this host (unlike MerchantGateway's
    // testnet-tier split) — flagged for whoever composes a non-Development deployment of OperationsApi.
    builder.Services.AddInMemoryChainSource();
    builder.Services.AddInMemoryTransactionEngine();
    builder.Services.AddInMemorySigner();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Operations API v1"));
}

app.UseMiddleware<StaffBearerAuthMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapOpsAuthApi();
app.MapOpsRoleApi();
app.MapOpsAccountApi();
app.MapOpsAuditApi();
app.MapOpsMerchantApi();
app.MapOpsMerchantFeeApi();
app.MapOpsWalletApi();
app.MapOpsPaymentIntentApi();
app.MapOpsTransactionApi();
app.MapOpsDepositTransactionApi();
app.MapOpsWithdrawalTransactionApi();
app.MapOpsCallbackApi();
app.MapOpsWithdrawalApprovalApi();
app.MapOpsWithdrawalFundingApi();

app.Run();
