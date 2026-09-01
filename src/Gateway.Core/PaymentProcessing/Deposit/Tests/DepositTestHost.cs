using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Tests;

/// <summary>
/// Real-SQL-Server fixture for the Deposit module. Drives the module against the in-memory chain source
/// (the same DI seam a JSON-RPC adapter plugs into) and a fake wallet directory, so the money-safe
/// detection/confirmation/reorg logic is exercised deterministically without a node.
/// </summary>
public abstract class DepositTestHost : IAsyncLifetime
{
    private const string DbName = "CpeDepositTests";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    protected static DepositDbContext Context() =>
        new(new DbContextOptionsBuilder<DepositDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    protected static DepositDetectionService Detection(
        DepositDbContext context, InMemoryChainSource chain, IWalletDirectory wallets, DepositPolicy policy,
        IMerchantFeeSchedule? feeSchedule = null, IDepositKindResolver? depositKinds = null) =>
        new(chain, chain, wallets, new DepositRepository(context), new ScanCursorStore(context, TimeProvider.System),
            new StubPolicyProvider(policy), feeSchedule ?? new NoFeeSchedule(),
            depositKinds ?? new StubDepositKindResolver(), TimeProvider.System,
            NullLogger<DepositDetectionService>.Instance);

    /// <summary>No invoice is waiting on the address, so every detected transfer is a customer deposit —
    /// the default for tests that are not about top-up. Pass a kind to make one a merchant top-up.</summary>
    protected sealed class StubDepositKindResolver(string? kind = null) : IDepositKindResolver
    {
        public Task<string?> FindWaitingKindAsync(Guid walletId, CancellationToken cancellationToken = default) =>
            Task.FromResult(kind);
    }

    protected static DepositConfirmationService Confirmation(
        DepositDbContext context, InMemoryChainSource chain, DepositPolicy policy) =>
        new(chain, new DepositRepository(context), new StubPolicyProvider(policy),
            TimeProvider.System, NullLogger<DepositConfirmationService>.Instance);

    public async ValueTask InitializeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    protected sealed class FakeWalletDirectory : IWalletDirectory
    {
        private readonly Dictionary<(Chain, string), WalletOwnership> _byAddress = [];

        public FakeWalletDirectory Register(WalletOwnership ownership)
        {
            _byAddress[(ownership.Chain, ownership.Address)] = ownership;
            return this;
        }

        public Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(_byAddress.GetValueOrDefault((chain, address)));

        public Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
            Task.FromResult<WalletOwnership?>(null);

        public Task<IReadOnlyList<AvailableWallet>> ListAssignedWalletsAsync(
            Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AvailableWallet>>([]);

        public Task<IReadOnlyList<ReceivingDepositAddress>> ListReceivingDepositAddressesAsync(
            Chain chain, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReceivingDepositAddress>>([]);
    }

    protected sealed class StubPolicyProvider(DepositPolicy policy) : IDepositPolicyProvider
    {
        public DepositPolicy For(Chain chain) => policy;
    }

    /// <summary>A merchant priced at a flat deposit fee — the deposit is priced at <paramref name="depositFee"/>
    /// at detection. An unpriced merchant (the default) uses <see cref="NoFeeSchedule"/>.</summary>
    protected sealed class FixedFeeSchedule(BigInteger depositFee) : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(depositFee);

        /// <summary>These fakes exercise paths unrelated to top-up pricing, so a top-up is quoted free.</summary>
        public Task<BigInteger> QuoteTopUpFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);
    }

    protected sealed class NoFeeSchedule : IMerchantFeeSchedule
    {
        public Task<BigInteger> QuoteDepositFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        /// <summary>These fakes exercise paths unrelated to top-up pricing, so a top-up is quoted free.</summary>
        public Task<BigInteger> QuoteTopUpFeeAsync(Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);

        public Task<BigInteger> QuoteWithdrawalFeeAsync(Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
            Task.FromResult(BigInteger.Zero);
    }
}
