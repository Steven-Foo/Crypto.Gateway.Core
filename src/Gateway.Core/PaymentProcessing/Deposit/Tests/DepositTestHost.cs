using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;
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
        DepositDbContext context, InMemoryChainSource chain, IWalletDirectory wallets, DepositPolicy policy) =>
        new(chain, chain, wallets, new DepositRepository(context), new ScanCursorStore(context, TimeProvider.System),
            new StubPolicyProvider(policy), TimeProvider.System, NullLogger<DepositDetectionService>.Instance);

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
    }

    protected sealed class StubPolicyProvider(DepositPolicy policy) : IDepositPolicyProvider
    {
        public DepositPolicy For(Chain chain) => policy;
    }
}
