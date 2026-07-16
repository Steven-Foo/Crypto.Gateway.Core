using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Handlers;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Locking;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Tests;

/// <summary>
/// The whole money-in path across two modules and two schemas, on a real SQL Server: an in-memory chain
/// transfer is detected and confirmed by Deposit, which writes a <see cref="DepositConfirmed"/> to its
/// outbox; that event, deserialized exactly as the dispatcher would, drives the Ledger handler and
/// credits the merchant. This is the contract that ties the two modules together — proven end to end.
/// </summary>
public sealed class DepositToLedgerTests : IAsyncLifetime
{
    private const string DbName = "CpeDepositLedgerTests";
    private const string Address = "TWatchedAddr";
    private static readonly Guid WalletId = Guid.CreateVersion7();
    private static readonly Guid MerchantId = Guid.CreateVersion7();
    private static readonly Guid AssetId = Guid.CreateVersion7();
    private static readonly BigInteger Amount = BigInteger.Parse("1000000");
    private static readonly DepositPolicy Policy = new(CreditStrategy.Confirmations, 3, BigInteger.Parse("1000"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static DepositDbContext DepositContext() =>
        new(new DbContextOptionsBuilder<DepositDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    private static LedgerDbContext LedgerContext() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    public async ValueTask InitializeAsync()
    {
        await using (var deposit = DepositContext())
        {
            await deposit.Database.EnsureDeletedAsync(Ct);
            await deposit.Database.EnsureCreatedAsync(Ct); // creates the DB + deposit schema
        }
        await using (var ledger = LedgerContext())
            await ledger.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(Ct); // ledger schema in the same DB
    }

    public async ValueTask DisposeAsync()
    {
        await using var deposit = DepositContext();
        await deposit.Database.EnsureDeletedAsync(Ct);
    }

    [Fact]
    public async Task A_confirmed_deposit_credits_the_merchant_in_the_ledger()
    {
        await DetectAndConfirmAsync();

        var confirmed = await DequeueEventAsync<DepositConfirmed>("DepositConfirmed");
        await using (var ledger = LedgerContext())
            await new DepositConfirmedHandler(Poster(ledger), new NoFeeSchedule()).HandleAsync(confirmed, Ct);

        (await MerchantBalanceAsync()).ShouldBe(Amount); // the deposit landed on the merchant's ledger balance
    }

    [Fact]
    public async Task A_confirmed_deposit_with_a_fee_credits_the_net_and_books_the_fee_as_revenue()
    {
        await DetectAndConfirmAsync();
        var fee = BigInteger.Parse("6000");

        var confirmed = await DequeueEventAsync<DepositConfirmed>("DepositConfirmed");
        await using (var ledger = LedgerContext())
            await new DepositConfirmedHandler(Poster(ledger), new FixedDepositFeeSchedule(fee)).HandleAsync(confirmed, Ct);

        (await MerchantBalanceAsync()).ShouldBe(Amount - fee);          // merchant nets gross − fee
        (await FeeRevenueBalanceAsync()).ShouldBe(fee);                 // platform keeps the fee as revenue
    }

    [Fact]
    public async Task An_orphaned_deposit_reverses_the_credit_back_to_zero()
    {
        await DetectAndConfirmAsync();

        var confirmed = await DequeueEventAsync<DepositConfirmed>("DepositConfirmed");
        await using (var ledger = LedgerContext())
            await new DepositConfirmedHandler(Poster(ledger), new NoFeeSchedule()).HandleAsync(confirmed, Ct);

        // Reorg orphans the confirmed deposit → DepositOrphaned → Ledger reverses.
        var chain = _chain!;
        chain.ReplaceBlock(Chain.Tron, 100, "h100_reorged");
        await using (var ctx = DepositContext())
            await Confirmation(ctx, chain).TrackOnceAsync(Chain.Tron, Ct);

        var orphaned = await DequeueEventAsync<DepositOrphaned>("DepositOrphaned");
        await using (var ledger = LedgerContext())
            await new DepositOrphanedHandler(Poster(ledger), new NoFeeSchedule()).HandleAsync(orphaned, Ct);

        (await MerchantBalanceAsync()).ShouldBe(BigInteger.Zero);
    }

    private InMemoryChainSource? _chain;

    private async Task DetectAndConfirmAsync()
    {
        _chain = new InMemoryChainSource();
        _chain.AddBlock(Chain.Tron, 100, "h100",
            new DetectedTransfer(Chain.Tron, Address, AssetId, Amount, "0xtx100", 0, 100, "h100"));

        var wallets = new StubWalletDirectory();

        await using (var ctx = DepositContext())
        {
            var detection = new DepositDetectionService(
                _chain, _chain, wallets, new DepositRepository(ctx), new ScanCursorStore(ctx, TimeProvider.System),
                new StubPolicy(), TimeProvider.System, NullLogger<DepositDetectionService>.Instance);
            await detection.ScanOnceAsync(Chain.Tron, Ct);
        }

        _chain.AddBlock(Chain.Tron, 101, "h101");
        _chain.AddBlock(Chain.Tron, 102, "h102");

        await using (var ctx = DepositContext())
            await Confirmation(ctx, _chain).TrackOnceAsync(Chain.Tron, Ct);
    }

    private static DepositConfirmationService Confirmation(DepositDbContext ctx, InMemoryChainSource chain) =>
        new(chain, new DepositRepository(ctx), new StubPolicy(), TimeProvider.System, NullLogger<DepositConfirmationService>.Instance);

    private static ILedgerPoster Poster(LedgerDbContext ledger) =>
        new LedgerPoster(
            new LedgerAccountStore(ledger, TimeProvider.System),
            new LedgerPostingStore(ledger, new NoOpLock(), TimeProvider.System, NullLogger<LedgerPostingStore>.Instance),
            TimeProvider.System);

    private static async Task<T> DequeueEventAsync<T>(string typeMarker)
    {
        await using var ctx = DepositContext();
        var message = await ctx.OutboxMessages
            .Where(m => m.Type.Contains(typeMarker))
            .OrderByDescending(m => EF.Property<long>(m, "Seq"))
            .FirstAsync(Ct);

        var type = Type.GetType(message.Type) ?? throw new InvalidOperationException($"Cannot resolve event type {message.Type}");
        return (T)JsonSerializer.Deserialize(message.Content, type)!;
    }

    private static async Task<BigInteger> MerchantBalanceAsync()
    {
        await using var ledger = LedgerContext();
        return (await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == AccountType.MerchantLiability && x.a.OwnerId == MerchantId && x.a.AssetId == AssetId)
            .Select(x => x.b.Balance)
            .SingleAsync(Ct));
    }

    private async Task<BigInteger> FeeRevenueBalanceAsync()
    {
        await using var ledger = LedgerContext();
        return await ledger.AccountBalances
            .Join(ledger.Accounts, b => b.Id, a => a.Id, (b, a) => new { b, a })
            .Where(x => x.a.AccountType == AccountType.FeeRevenue && x.a.AssetId == AssetId)
            .Select(x => x.b.Balance)
            .SingleOrDefaultAsync(Ct);
    }

    /// <summary>A priced merchant: a flat deposit fee taken off the top.</summary>
    private sealed class FixedDepositFeeSchedule(BigInteger depositFee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(depositFee);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<Result<BigInteger>> GrossUpDepositAsync(Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(netTarget));
    }

    private sealed class StubWalletDirectory : IWalletDirectory
    {
        public Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<WalletOwnership?>(address == Address
                ? new WalletOwnership(WalletId, Guid.CreateVersion7(), Chain.Tron, Address, "Deposit", MerchantId, IsActive: true)
                : null);

        public Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
            Task.FromResult<WalletOwnership?>(null);
    }

    private sealed class StubPolicy : Application.Abstractions.IDepositPolicyProvider
    {
        public DepositPolicy For(Chain chain) => Policy;
    }

    /// <summary>Unpriced merchant: the deposit is credited in full (no fee split).</summary>
    private sealed class NoFeeSchedule : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

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
