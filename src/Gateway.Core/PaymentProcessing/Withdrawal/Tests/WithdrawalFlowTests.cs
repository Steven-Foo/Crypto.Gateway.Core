using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
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

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The whole money-out path on a real SQL Server: request → ledger reserve (merchant debited) → build →
/// sign → broadcast → confirm → ledger settle (funds leave custody, fee → revenue). Plus the guard paths:
/// insufficient balance is refused with no debit, rejection releases the reserve, and the request is
/// idempotent. Signing goes through the (fake) <see cref="ISigner"/> port — no key touches the flow.
/// </summary>
public sealed class WithdrawalFlowTests : IAsyncLifetime
{
    private const string DbName = "CpeWithdrawalFlowTests";
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Fee = BigInteger.Parse("100000");
    private static readonly WithdrawalPolicy Policy =
        new(Minimum: BigInteger.Zero, Maximum: null, Fee: Fee, ApprovalThreshold: BigInteger.Parse("5000000"), Confirmations: 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockFactory, NoOpLock>();

        services.AddDbContext<WithdrawalDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());
        services.AddDbContext<LedgerDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());

        // Ledger
        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();
        services.AddScoped<LedgerPoster>();
        services.AddScoped<ILedgerPoster>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IWithdrawalLedger>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IIntegrationEventHandler<WithdrawalConfirmed>, WithdrawalConfirmedHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalFailed>, WithdrawalFailedHandler>();
        services.AddScoped<IEventBus, InProcessEventBus>();

        // Withdrawal + fakes
        services.AddScoped<IWithdrawalRepository, WithdrawalRepository>();
        services.AddScoped<IWithdrawalRequestService, WithdrawalRequestService>();
        services.AddScoped<IWithdrawalApprovalService, WithdrawalApprovalService>();
        services.AddScoped<WithdrawalProcessingService>();
        services.AddScoped<WithdrawalConfirmationService>();
        services.AddSingleton<IWithdrawalPolicyProvider>(new StubPolicy());
        services.AddSingleton<IHotWalletProvider>(new StubHotWallet());
        services.AddSingleton<IMerchantDirectory>(new FakeMerchants());
        services.AddSingleton<IMerchantFeeSchedule>(new FakeFees(Fee));
        services.AddSingleton<InMemoryTransactionEngine>();
        services.AddSingleton<ITransactionBuilder>(sp => sp.GetRequiredService<InMemoryTransactionEngine>());
        services.AddSingleton<ITransactionBroadcaster>(sp => sp.GetRequiredService<InMemoryTransactionEngine>());
        services.AddSingleton<IChainStatusReader>(new StubChainStatus());
        services.AddSingleton<ISigner, InMemorySigner>();

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var w = scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>();
        await w.Database.EnsureDeletedAsync(Ct);
        await w.Database.EnsureCreatedAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database
            .GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Database.EnsureDeletedAsync(Ct);
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task The_full_money_out_path_debits_reserve_then_settles_custody_and_fee()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000")); // 10 USDT
        var amount = BigInteger.Parse("3000000"); // below approval threshold → auto-approved

        var request = await RequestAsync(amount, "idem-happy");
        request.IsSuccess.ShouldBeTrue();
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.Approved));

        // Merchant was debited amount+fee at reserve; held in clearing.
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Parse("3100000"));

        await ProcessAsync();  // build → sign → broadcast
        await ConfirmAsync();  // → WithdrawalConfirmed
        await DispatchAsync(); // → Ledger settle

        await using (var scope = _provider.CreateAsyncScope())
        {
            var w = await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct);
            w.Status.ShouldBe(WithdrawalStatus.Confirmed);
            w.TransactionHash.ShouldNotBeNull();
        }

        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);         // cleared
        (await BalanceAsync(AccountType.TreasuryAsset, null)).ShouldBe(BigInteger.Parse("7000000"));  // amount left custody
        (await BalanceAsync(AccountType.FeeRevenue, null)).ShouldBe(Fee);                             // fee kept as revenue
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000"));
    }

    [Fact]
    public async Task A_withdrawal_beyond_the_balance_is_refused_with_no_debit()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("1000000"));

        var request = await RequestAsync(BigInteger.Parse("5000000"), "idem-broke");

        request.IsFailure.ShouldBeTrue();
        request.Error!.Code.ShouldBe(WithdrawalErrors.InsufficientBalance.Code);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("1000000")); // untouched
    }

    [Fact]
    public async Task Rejecting_an_approval_releases_the_reserved_funds()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));
        var amount = BigInteger.Parse("6000000"); // above threshold → PendingApproval

        var request = await RequestAsync(amount, "idem-reject");
        request.Value.Status.ShouldBe(nameof(WithdrawalStatus.PendingApproval));
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("3900000")); // reserved

        await using (var scope = _provider.CreateAsyncScope())
        {
            var id = (await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct)).Id;
            (await scope.ServiceProvider.GetRequiredService<IWithdrawalApprovalService>().RejectAsync(id, "ops", "manual", Ct))
                .IsSuccess.ShouldBeTrue();
        }

        await DispatchAsync(); // WithdrawalFailed → Ledger release

        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("10000000")); // fully restored
    }

    [Fact]
    public async Task The_request_is_idempotent_on_the_client_key()
    {
        await SeedMerchantBalanceAsync(BigInteger.Parse("10000000"));

        var first = await RequestAsync(BigInteger.Parse("3000000"), "idem-dupe");
        var second = await RequestAsync(BigInteger.Parse("3000000"), "idem-dupe");

        first.Value.WithdrawalId.ShouldBe(second.Value.WithdrawalId); // same withdrawal
        await using var scope = _provider.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.CountAsync(Ct)).ShouldBe(1);
        (await BalanceAsync(AccountType.MerchantLiability, Merchant)).ShouldBe(BigInteger.Parse("6900000")); // debited once
    }

    // ── helpers ──

    private async Task SeedMerchantBalanceAsync(BigInteger amount)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILedgerPoster>()
            .CreditDepositAsync(new CreditDepositCommand(Guid.CreateVersion7(), Merchant, Asset, amount), Ct);
    }

    private async Task<Result<WithdrawalResult>> RequestAsync(BigInteger amount, string idempotencyKey)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWithdrawalRequestService>()
            .RequestAsync(new RequestWithdrawalCommand(Merchant, Asset, Chain.Tron, "TDestination", amount, idempotencyKey), Ct);
    }

    private async Task ProcessAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WithdrawalProcessingService>().ProcessOnceAsync(Ct);
    }

    private async Task ConfirmAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WithdrawalConfirmationService>().TrackOnceAsync(Ct);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = new OutboxDispatcher<WithdrawalDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>(), new NoOpLock(), TimeProvider.System,
            NullLogger<OutboxDispatcher<WithdrawalDbContext>>.Instance);
        await dispatcher.DispatchPendingAsync(Ct);
    }

    private async Task<BigInteger> BalanceAsync(AccountType type, Guid? ownerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        return await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == type && x.a.OwnerId == ownerId && x.a.AssetId == Asset)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);
    }

    private sealed class StubPolicy : IWithdrawalPolicyProvider
    {
        public WithdrawalPolicy For(Chain chain) => Policy;
    }

    private sealed class StubHotWallet : IHotWalletProvider
    {
        public HotWallet For(Chain chain) => new("THotWallet", "kms://tron/hot/0");
    }

    private sealed class FakeMerchants : IMerchantDirectory
    {
        public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(new MerchantSummary(merchantId, "ACME", "Acme", null, CanTransact: true));

        public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(null);
    }

    /// <summary>Per-merchant pricing: a flat withdrawal fee, matching this suite's expectations.</summary>
    private sealed class FakeFees(BigInteger withdrawalFee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(withdrawalFee);

        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class StubChainStatus : IChainStatusReader
    {
        public Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default) => Task.FromResult(1000L);
        public Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<BlockRef?>(new BlockRef(blockNumber, "0xblock"));
        public Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default) => Task.FromResult(1000L);
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
