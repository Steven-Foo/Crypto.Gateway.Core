using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Mongo;
using CryptoPaymentEngine.SharedKernel;
using MongoDB.Driver;
using Shouldly;
using Testcontainers.MongoDb;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The codebase's first MongoDB store, proven against a real Mongo (Testcontainers): a resource snapshot
/// upserts and reads back with BigInteger amounts intact (stored as strings, so no precision loss), and a
/// re-upsert overwrites the wallet's single current document rather than duplicating it.
/// </summary>
public sealed class MongoWalletResourceStoreTests : IAsyncLifetime
{
    private MongoDbContainer? _container;
    private MongoWalletResourceStore _store = null!;
    private bool _available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        try
        {
            // Build() itself validates (and caches) Docker availability, so it must be inside the guard —
            // no Docker on this host means skip, not fail. Runs in CI / wherever Docker is present.
            _container = new MongoDbBuilder().Build();
            await _container.StartAsync(Ct);
            var database = new MongoClient(_container.GetConnectionString()).GetDatabase("energy_test");
            _store = new MongoWalletResourceStore(database);
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is null)
            return;

        try
        {
            await _container.DisposeAsync();
        }
        catch
        {
            // Docker unavailable — there is nothing to tear down.
        }
    }

    private static WalletResourceSnapshot Snapshot(Guid walletId, BigInteger energyAvailable, ResourceHealth health) =>
        new(walletId, Chain.Tron, "THotWallet", "HotWithdrawal", health,
            energyAvailable, energyAvailable, 0, 0, 0, 0, 0, 0,
            BigInteger.Parse("123456789012345678901234567890"), 5_000_000, 1_000_000, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Upsert_then_get_round_trips_with_exact_big_integers()
    {
        Assert.SkipUnless(_available, "Docker is not available for the Mongo integration test.");
        var walletId = Guid.CreateVersion7();
        await _store.UpsertAsync(Snapshot(walletId, 3_000_000, ResourceHealth.Low), Ct);

        var read = await _store.GetAsync(walletId, Ct);

        read.ShouldNotBeNull();
        read.WalletId.ShouldBe(walletId);
        read.Chain.ShouldBe(Chain.Tron);
        read.Health.ShouldBe(ResourceHealth.Low);
        read.EnergyAvailable.ShouldBe(3_000_000);
        read.AvailableTrxBalance.ShouldBe(BigInteger.Parse("123456789012345678901234567890"));
        read.TargetEnergy.ShouldBe(5_000_000);
        read.MinimumEnergy.ShouldBe(1_000_000);
    }

    [Fact]
    public async Task Upsert_overwrites_the_current_snapshot_for_a_wallet()
    {
        Assert.SkipUnless(_available, "Docker is not available for the Mongo integration test.");
        var walletId = Guid.CreateVersion7();
        await _store.UpsertAsync(Snapshot(walletId, 3_000_000, ResourceHealth.Low), Ct);
        await _store.UpsertAsync(Snapshot(walletId, 500_000, ResourceHealth.Critical), Ct);

        var read = await _store.GetAsync(walletId, Ct);

        read.ShouldNotBeNull();
        read.Health.ShouldBe(ResourceHealth.Critical);
        read.EnergyAvailable.ShouldBe(500_000);
    }
}
