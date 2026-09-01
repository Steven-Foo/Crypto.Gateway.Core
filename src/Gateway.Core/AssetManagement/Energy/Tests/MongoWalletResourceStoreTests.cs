using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Mongo;
using CryptoPaymentEngine.SharedKernel;
using MongoDB.Driver;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The codebase's first MongoDB store, proven against a REAL Mongo — the local service by default, or
/// whatever <c>CPE_TEST_MONGO</c> points at (see <see cref="MongoTestDatabase"/>). A resource snapshot
/// upserts and reads back with BigInteger amounts intact (stored as strings, so no precision loss), and a
/// re-upsert overwrites the wallet's single current document rather than duplicating it.
/// </summary>
public sealed class MongoWalletResourceStoreTests : IAsyncLifetime
{
    private MongoClient _client = null!;
    private MongoWalletResourceStore _store = null!;
    private bool _available;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // No Mongo reachable means skip, not fail — the same courtesy the SQL tests extend when LocalDB is
        // absent. The client is configured to give up in seconds rather than the driver's default 30s.
        _client = MongoTestDatabase.CreateClient();
        _available = await MongoTestDatabase.IsAvailableAsync(_client, Ct);
        if (!_available)
            return;

        // A dedicated test database, dropped on the way in as well as out: a previous run killed mid-test
        // must not leave documents that make this one pass (or fail) for the wrong reason.
        await _client.DropDatabaseAsync(MongoTestDatabase.DatabaseName, Ct);
        _store = new MongoWalletResourceStore(_client.GetDatabase(MongoTestDatabase.DatabaseName));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_available)
            return;

        try
        {
            await _client.DropDatabaseAsync(MongoTestDatabase.DatabaseName, CancellationToken.None);
        }
        catch
        {
            // Teardown is best-effort; a dropped connection here must not fail an otherwise green run.
        }
    }

    private static WalletResourceSnapshot Snapshot(Guid walletId, BigInteger energyAvailable, ResourceHealth health) =>
        new(walletId, Chain.Tron, "THotWallet", "HotWithdrawal", health,
            energyAvailable, energyAvailable, 0, 0, 0, 0, 0, 0,
            BigInteger.Parse("123456789012345678901234567890"), 5_000_000, 1_000_000, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Upsert_then_get_round_trips_with_exact_big_integers()
    {
        Assert.SkipUnless(_available, $"No MongoDB at {MongoTestDatabase.ConnectionString} — start the local service or set CPE_TEST_MONGO.");
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
        Assert.SkipUnless(_available, $"No MongoDB at {MongoTestDatabase.ConnectionString} — start the local service or set CPE_TEST_MONGO.");
        var walletId = Guid.CreateVersion7();
        await _store.UpsertAsync(Snapshot(walletId, 3_000_000, ResourceHealth.Low), Ct);
        await _store.UpsertAsync(Snapshot(walletId, 500_000, ResourceHealth.Critical), Ct);

        var read = await _store.GetAsync(walletId, Ct);

        read.ShouldNotBeNull();
        read.Health.ShouldBe(ResourceHealth.Critical);
        read.EnergyAvailable.ShouldBe(500_000);
    }
}
