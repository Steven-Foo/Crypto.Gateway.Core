using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
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
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// Level 3, live + persistent: the full withdrawal money-out path against the <b>real</b> Nile node AND a
/// <b>persistent</b> SQL database, so the resulting rows can be inspected by hand in DBeaver. It does a real
/// on-chain TRC-20 transfer and leaves the withdrawal + ledger rows in place (it does NOT drop the database on
/// teardown). Gated on the same <c>CPE_NILE_*</c> variables as <see cref="WithdrawalNileLiveTests"/> plus an
/// optional <c>CPE_NILE_DB</c> connection string; skipped otherwise. See <c>docs/withdrawal-testnet.md</c>.
/// </summary>
[Trait("Category", "LiveTestnet")]
public sealed class WithdrawalNileDbFlowTests : IAsyncLifetime
{
    private const string DbName = "CpeNileWithdrawalDemo";
    private const string KeyReference = "kms://tron/hot/nile";

    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();
    private static readonly BigInteger Fee = BigInteger.Parse("100000"); // 0.1 USDT platform fee (demo)
    private static readonly WithdrawalPolicy Policy =
        new(Minimum: BigInteger.Zero, Maximum: null, Fee: Fee, ApprovalThreshold: BigInteger.Parse("1000000000"), Confirmations: 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_NILE_DB") is { Length: > 0 } configured
            ? configured
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private ServiceProvider _provider = null!;
    private bool _enabled;

    public async ValueTask InitializeAsync()
    {
        var rpcUrl = Env("CPE_NILE_RPC");
        var privateKeyHex = Env("CPE_NILE_PRIVKEY");
        var from = Env("CPE_NILE_FROM");
        var contract = Env("CPE_NILE_CONTRACT");
        var to = Env("CPE_NILE_TO");
        _enabled = new[] { rpcUrl, privateKeyHex, from, contract, to }.All(v => !string.IsNullOrWhiteSpace(v));
        if (!_enabled)
            return;

        var http = new HttpClient { BaseAddress = new Uri(rpcUrl!.EndsWith('/') ? rpcUrl : rpcUrl + "/") };
        if (Env("CPE_NILE_APIKEY") is { Length: > 0 } apiKey)
            http.DefaultRequestHeaders.Add("TRON-PRO-API-KEY", apiKey);
        var rpc = new TronRpc(http);
        var catalog = new SingleAssetCatalog(contract!);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDistributedLockFactory, NoOpLock>();

        services.AddDbContext<WithdrawalDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());
        services.AddDbContext<LedgerDbContext>(o => o.UseSqlServer(ConnectionString).UseBigIntegerMoney());

        services.AddScoped<ILedgerAccountStore, LedgerAccountStore>();
        services.AddScoped<ILedgerPostingStore, LedgerPostingStore>();
        services.AddScoped<LedgerPoster>();
        services.AddScoped<ILedgerPoster>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IWithdrawalLedger>(sp => sp.GetRequiredService<LedgerPoster>());
        services.AddScoped<IIntegrationEventHandler<WithdrawalConfirmed>, WithdrawalConfirmedHandler>();
        services.AddScoped<IIntegrationEventHandler<WithdrawalFailed>, WithdrawalFailedHandler>();
        services.AddScoped<IEventBus, InProcessEventBus>();

        services.AddScoped<IWithdrawalRepository, WithdrawalRepository>();
        services.AddScoped<IWithdrawalRequestService, WithdrawalRequestService>();
        services.AddScoped<WithdrawalProcessingService>();
        services.AddScoped<WithdrawalConfirmationService>();
        services.AddSingleton<IWithdrawalPolicyProvider>(new StubPolicy());
        services.AddSingleton<IHotWalletProvider>(new StubHotWallet(from!));
        services.AddSingleton<IMerchantDirectory>(new FakeMerchants());
        services.AddSingleton<IMerchantFeeSchedule>(new FakeFees(Fee));

        // Real Nile stack: real builder + real signer + real broadcaster + real chain status (tip/finality).
        // TronRpc implements both node interfaces — register it under each.
        services.AddSingleton<ITronRpc>(rpc);
        services.AddSingleton<ITronTxRpc>(rpc);
        services.AddSingleton<IAssetCatalog>(catalog);
        services.AddSingleton(new TronOptions());
        services.AddSingleton<ITransactionBuilder, TronTransactionBuilder>();
        services.AddSingleton<ITransactionBroadcaster, TronTransactionBroadcaster>();
        services.AddSingleton<IChainStatusReader>(sp => new TronChainAdapter(rpc, catalog, NullLogger<TronChainAdapter>.Instance));
        services.AddSingleton<ISigner>(new TronSigner(
            InMemorySecretProvider.FromStrings(new Dictionary<string, string> { [KeyReference] = privateKeyHex! }),
            NullLogger<TronSigner>.Instance));

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var w = scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>();
        await w.Database.EnsureDeletedAsync(Ct); // fresh demo DB each run…
        await w.Database.EnsureCreatedAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database
            .GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct);
    }

    // …but deliberately NOT dropped on teardown, so the rows remain for manual inspection.
    public ValueTask DisposeAsync()
    {
        _provider?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Full_withdrawal_flow_against_nile_persists_rows_and_settles()
    {
        if (!_enabled)
        {
            Assert.Skip("Set CPE_NILE_* (and optionally CPE_NILE_DB) to run the persistent live-Nile DB demo.");
            return;
        }

        var amountRaw = Env("CPE_NILE_AMOUNT");
        var amount = BigInteger.Parse(string.IsNullOrWhiteSpace(amountRaw) ? "1000000" : amountRaw);

        // A merchant balance to withdraw against (deposit credit): custody + liability seeded well above the amount.
        await SeedMerchantBalanceAsync(amount * 10);

        var request = await RequestAsync(amount, $"nile-demo-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        request.IsSuccess.ShouldBeTrue(request.Error?.Message);

        await ProcessAsync(); // real build → real sign → persist blob → real broadcast to Nile

        var broadcast = await SingleWithdrawalAsync();
        broadcast.Status.ShouldBe(WithdrawalStatus.Broadcast);
        broadcast.TransactionHash.ShouldNotBeNullOrWhiteSpace();
        Report($"txid={broadcast.TransactionHash}");
        Report($"explorer=https://nile.tronscan.org/#/transaction/{broadcast.TransactionHash}");

        // Poll the real node until it is mined + buried to the confirmation depth, then settle.
        for (var attempt = 0; attempt < 40 && (await SingleWithdrawalAsync()).Status != WithdrawalStatus.Confirmed; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            await ConfirmAsync();
        }
        await DispatchAsync();

        var settled = await SingleWithdrawalAsync();
        settled.Status.ShouldBe(WithdrawalStatus.Confirmed);

        Report($"db={ConnectionString}");
        Report($"withdrawal_id={settled.Id}");
        Report($"merchant_liability={await BalanceAsync(AccountType.MerchantLiability, Merchant)}");
        Report($"treasury_asset={await BalanceAsync(AccountType.TreasuryAsset, null)}");
        Report($"fee_revenue={await BalanceAsync(AccountType.FeeRevenue, null)}");
        Report($"withdrawal_clearing={await BalanceAsync(AccountType.WithdrawalClearing, null)}");

        (await BalanceAsync(AccountType.WithdrawalClearing, null)).ShouldBe(BigInteger.Zero);
        (await BalanceAsync(AccountType.FeeRevenue, null)).ShouldBe(Fee);
    }

    private static void Report(string line)
    {
        if (Env("CPE_NILE_OUT") is { Length: > 0 } path)
            File.AppendAllText(path, line + Environment.NewLine);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    // ── helpers ──

    private async Task<WithdrawalEntity> SingleWithdrawalAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WithdrawalDbContext>().Withdrawals.SingleAsync(Ct);
    }

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
            .RequestAsync(new RequestWithdrawalCommand(Merchant, Asset, Chain.Tron, Env("CPE_NILE_TO")!, amount, idempotencyKey), Ct);
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

    private sealed class SingleAssetCatalog(string contractAddress) : IAssetCatalog
    {
        private readonly AssetDto _asset = new(Asset, Chain.Tron, "USDT", contractAddress, 6, IsNative: false);
        public Task<AssetDto?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<AssetDto?>(id == Asset ? _asset : null);
        public Task<AssetDto?> FindAsync(Chain chain, string symbol, CancellationToken ct = default) => Task.FromResult<AssetDto?>(null);
        public Task<IReadOnlyList<AssetDto>> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AssetDto>>([_asset]);
    }

    private sealed class StubPolicy : IWithdrawalPolicyProvider
    {
        public WithdrawalPolicy For(Chain chain) => Policy;
    }

    private sealed class StubHotWallet(string address) : IHotWalletProvider
    {
        public HotWallet For(Chain chain) => new(address, KeyReference);
    }

    private sealed class FakeMerchants : IMerchantDirectory
    {
        public Task<MerchantSummary?> FindByIdAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(new MerchantSummary(merchantId, "ACME", "Acme", null, CanTransact: true));
        public Task<MerchantSummary?> FindByCodeAsync(string merchantCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MerchantSummary?>(null);
    }

    private sealed class FakeFees(BigInteger withdrawalFee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);
        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(withdrawalFee);
        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
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
