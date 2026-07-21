using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;
using CryptoPaymentEngine.Infrastructure.Events;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Outbox;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Tests;

/// <summary>
/// The money-in composition capstone on real SQL Server: a single <see cref="DepositConfirmed"/> fans out to
/// BOTH the Ledger (immutable credit) AND PaymentIntent (invoice match), and the resulting
/// <see cref="PaymentIntentMatched"/> flows through the outbox to the Notification callback. This proves the
/// modules wire together through the real event bus + outbox — the one thing the per-module tests can't show.
/// </summary>
public sealed class MoneyInCompositionTests : IAsyncLifetime
{
    private const string DbName = "CpeMoneyInCompositionTests";
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger OneUsdt = BigInteger.Parse("1000000");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private readonly CapturingSender _sender = new();
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockFactory, NoOpLock>();

        services.AddDbContext<LedgerDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());
        services.AddDbContext<PaymentIntentDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());

        services.AddScoped<IEventBus, InProcessEventBus>();

        // Ledger: credit handler for DepositConfirmed. Unpriced merchant → no deposit fee (full credit).
        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();
        services.AddScoped<LedgerPoster>();
        services.AddScoped<ILedgerPoster>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddSingleton<IMerchantFeeSchedule>(new NoFeeSchedule());
        services.AddScoped<IIntegrationEventHandler<DepositConfirmed>, DepositConfirmedHandler>();

        // PaymentIntent: the SECOND handler for the same DepositConfirmed — the fan-out. This test drives
        // the intent straight into the DB and never calls CreateAsync, so it never exercises address
        // reservation itself — only the release-on-match call the handler makes, hence the no-op lock.
        services.AddSingleton<IWalletReservationLock>(new NoOpWalletReservationLock());
        services.AddScoped<IPaymentIntentRepository, PaymentIntentRepository>();
        services.AddScoped<IIntegrationEventHandler<DepositConfirmed>, PaymentIntentMatchHandler>();

        // Notification: consumes PaymentIntentMatched → signed callback (captured).
        services.AddSingleton<IWebhookSender>(_sender);
        services.AddSingleton<IMerchantCallbackSigner>(new StubSigner());
        services.AddSingleton<IAssetCatalog>(new StubCatalog());
        services.AddScoped<IIntegrationEventHandler<PaymentIntentMatched>, DepositCallbackHandler>();

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await ledger.Database.EnsureDeletedAsync(Ct);
        await ledger.Database.EnsureCreatedAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<PaymentIntentDbContext>().Database
            .GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.EnsureDeletedAsync(Ct);
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_confirmed_deposit_credits_the_ledger_matches_the_invoice_and_fires_the_callback()
    {
        var walletId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        // A merchant invoice is waiting on this deposit address.
        var intent = PaymentIntentEntity.Create(
            Merchant, "tx-e2e", Chain.Tron, Asset, walletId, "TDepositAddress", OneUsdt, "https://merchant.test/cb",
            now.AddMinutes(30), now.AddMinutes(40), now).Value;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<PaymentIntentDbContext>();
            ctx.PaymentIntents.Add(intent);
            await ctx.SaveChangesAsync(Ct);
        }

        // One confirmed deposit, published as the Deposit outbox would.
        var deposit = new DepositConfirmed(
            Guid.CreateVersion7(), now, DepositId: Guid.CreateVersion7(), walletId, Merchant, Asset,
            AmountBaseUnits: "1000000", Chain.Tron, TransactionHash: "0xdeadbeef", OutputIndex: 0, ConfirmedAt: now);

        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(deposit, Ct);

        // Fan-out #1: the ledger credited the merchant (immutable, derived balance).
        (await MerchantLiabilityAsync()).ShouldBe(OneUsdt);

        // Fan-out #2: the invoice matched, and raised PaymentIntentMatched into its outbox.
        await using (var verify = _provider.CreateAsyncScope())
        {
            var matched = await verify.ServiceProvider.GetRequiredService<PaymentIntentDbContext>()
                .PaymentIntents.SingleAsync(Ct);
            matched.Status.ShouldBe(PaymentIntentStatus.Matched);
            matched.AmountMatched.ShouldBe(true);
        }

        // The outbox relays PaymentIntentMatched → the Notification callback.
        await DispatchPaymentIntentOutboxAsync();

        _sender.Body.ShouldNotBeNull();
        _sender.Url.ShouldBe("https://merchant.test/cb");
        using var doc = JsonDocument.Parse(_sender.Body!);
        doc.RootElement.GetProperty("transactionId").GetString().ShouldBe("tx-e2e");
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().ShouldBe("confirmed");
        data.GetProperty("txHash").GetString().ShouldBe("0xdeadbeef");
        data.GetProperty("amount").GetDecimal().ShouldBe(1m);
        data.GetProperty("amountMatched").GetBoolean().ShouldBeTrue();
    }

    private async Task DispatchPaymentIntentOutboxAsync()
    {
        var dispatcher = new OutboxDispatcher<PaymentIntentDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>(), new NoOpLock(), TimeProvider.System,
            NullLogger<OutboxDispatcher<PaymentIntentDbContext>>.Instance);
        await dispatcher.DispatchPendingAsync(Ct);
    }

    private async Task<BigInteger> MerchantLiabilityAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == AccountType.MerchantLiability && x.a.OwnerId == Merchant && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);
    }

    private sealed class CapturingSender : IWebhookSender
    {
        public string? Url;
        public string? Body;

        public Task<bool> SendAsync(string url, string body, string callbackType, string timestamp, string signatureHex, CancellationToken cancellationToken = default)
        {
            Url = url;
            Body = body;
            return Task.FromResult(true);
        }
    }

    private sealed class StubSigner : IMerchantCallbackSigner
    {
        public Task<Result<CallbackSignature>> SignAsync(Guid merchantId, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CallbackSignature("1700000000", "sig")));
    }

    private sealed class StubCatalog : IAssetCatalog
    {
        public Task<AssetDto?> FindByIdAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssetDto?>(new AssetDto(assetId, Chain.Tron, "USDT", "TContract", 6, false));

        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssetDto?>(new AssetDto(Asset, Chain.Tron, "USDT", "TContract", 6, false));

        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AssetDto>>([]);
    }

    /// <summary>An unpriced merchant: no deposit fee, no gross-up. Keeps this capstone focused on the fan-out.</summary>
    private sealed class NoFeeSchedule : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class NoOpWalletReservationLock : IWalletReservationLock
    {
        public Task<bool> TryReserveAsync(Guid walletId, string referenceId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task ReleaseAsync(Guid walletId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpLock : IDistributedLockFactory
    {
        public Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new Handle());

        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
