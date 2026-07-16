using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The core of Phase 5a: given a platform wallet, a policy, and observed resources, the monitor records a
/// classified snapshot + history. Composed with the real in-memory reader and capturing stores — no SQL/Mongo.
/// </summary>
public sealed class ResourceMonitorServiceTests
{
    private const string HotWallet = "THotWithdrawalWalletAddr0000000000";
    private static readonly Guid WalletId = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CapturingResourceStore : IWalletResourceStore
    {
        public WalletResourceSnapshot? Last;
        public int Upserts;
        public Task UpsertAsync(WalletResourceSnapshot snapshot, CancellationToken ct = default)
        {
            Last = snapshot;
            Upserts++;
            return Task.CompletedTask;
        }
        public Task<WalletResourceSnapshot?> GetAsync(Guid walletId, CancellationToken ct = default) =>
            Task.FromResult(Last);
    }

    private sealed class CapturingHistoryStore : IResourceHistoryStore
    {
        public int Appends;
        public Task AppendAsync(WalletResourceSnapshot snapshot, CancellationToken ct = default)
        {
            Appends++;
            return Task.CompletedTask;
        }
    }

    private static (ResourceMonitorService Service, CapturingResourceStore Store, CapturingHistoryStore History) Build(
        BigInteger energyAvailable, EnergyPolicy? policy, bool hasWallet = true)
    {
        var wallets = Substitute.For<IPlatformWalletDirectory>();
        wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PlatformWallet>)(hasWallet
                ? [new PlatformWallet(WalletId, Chain.Tron, HotWallet, "HotWithdrawal")]
                : []));

        var reader = new InMemoryAccountResourceReader(TimeProvider.System);
        reader.SetEnergyAvailable(Chain.Tron, HotWallet, energyAvailable);

        var policies = Substitute.For<IEnergyPolicyRepository>();
        policies.FindAsync(Chain.Tron, "HotWithdrawal", Arg.Any<CancellationToken>()).Returns(policy);

        var store = new CapturingResourceStore();
        var history = new CapturingHistoryStore();

        var service = new ResourceMonitorService(
            wallets, reader, policies, store, history, NullLogger<ResourceMonitorService>.Instance);

        return (service, store, history);
    }

    private static EnergyPolicy HotPolicy() =>
        EnergyPolicy.Create(Chain.Tron, "HotWithdrawal", 1_000_000, 5_000_000, 2_000_000, 500_000, false, false).Value;

    [Theory]
    [InlineData("500000", ResourceHealth.Critical)]
    [InlineData("3000000", ResourceHealth.Low)]
    [InlineData("9000000", ResourceHealth.Healthy)]
    public async Task Monitor_records_a_classified_snapshot_and_history(string available, ResourceHealth expected)
    {
        var (service, store, history) = Build(BigInteger.Parse(available), HotPolicy());

        await service.MonitorAsync(Chain.Tron, Ct);

        store.Upserts.ShouldBe(1);
        history.Appends.ShouldBe(1);
        store.Last!.Health.ShouldBe(expected);
        store.Last.WalletId.ShouldBe(WalletId);
        store.Last.EnergyAvailable.ShouldBe(BigInteger.Parse(available));
        store.Last.TargetEnergy.ShouldBe(5_000_000);
    }

    [Fact]
    public async Task Monitor_records_but_does_not_classify_when_no_policy_exists()
    {
        var (service, store, _) = Build(100, policy: null);

        await service.MonitorAsync(Chain.Tron, Ct);

        store.Upserts.ShouldBe(1);
        store.Last!.Health.ShouldBe(ResourceHealth.Healthy); // observed, not alerted
        store.Last.TargetEnergy.ShouldBeNull();
    }

    [Fact]
    public async Task Monitor_does_nothing_when_there_are_no_platform_wallets()
    {
        var (service, store, history) = Build(100, HotPolicy(), hasWallet: false);

        await service.MonitorAsync(Chain.Tron, Ct);

        store.Upserts.ShouldBe(0);
        history.Appends.ShouldBe(0);
    }
}
