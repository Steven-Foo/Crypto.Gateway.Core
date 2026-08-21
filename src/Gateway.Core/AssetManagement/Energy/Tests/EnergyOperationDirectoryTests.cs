using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The Ops read directory over energy operations, on real SQL Server. Proves the no-tracking search maps the
/// state machine correctly (kind/status names, exact sun amount beyond Int64 — decimal(38,0), §14), orders
/// newest-first, filters by kind/status, and summarises counts by status.
/// </summary>
public sealed class EnergyOperationDirectoryTests : IAsyncLifetime
{
    private const string DbName = "CpeEnergyOperationDirectoryTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static EnergyDbContext NewContext() =>
        new(new DbContextOptionsBuilder<EnergyDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

    public async ValueTask InitializeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    // Beyond Int64, to prove decimal(38,0), not a silently-capped converter (§7.2/§14).
    private static readonly BigInteger BigSun = BigInteger.Parse("12345678901234567890");

    private async Task SeedAsync()
    {
        await using var context = NewContext();

        // Distinct staking wallets + targets so the filtered in-flight unique indexes never collide.
        var stake = EnergyOperation.CreateStake(Guid.CreateVersion7(), Chain.Tron, "TStakeOwner", BigSun, T0).Value;

        var confirmed = EnergyOperation.CreateStake(Guid.CreateVersion7(), Chain.Tron, "TStakeOwner2", new BigInteger(5_000_000), T0.AddMinutes(1)).Value;
        confirmed.RecordSigned(Guid.CreateVersion7(), [1], T0.AddMinutes(2)).IsSuccess.ShouldBeTrue();
        confirmed.MarkBroadcast("tx-confirmed", T0.AddMinutes(3)).IsSuccess.ShouldBeTrue();
        confirmed.Confirm(T0.AddMinutes(4)).IsSuccess.ShouldBeTrue();

        var delegated = EnergyOperation.CreateDelegate(Guid.CreateVersion7(), Chain.Tron, "TDelegOwner", "TDepositAddr", new BigInteger(3_000_000), T0.AddMinutes(5)).Value;

        var failedTopUp = EnergyOperation.CreateTopUp(Guid.CreateVersion7(), Chain.Tron, "THubOwner", "TShortAddr", new BigInteger(1_000_000), T0.AddMinutes(6)).Value;
        failedTopUp.Fail("insufficient hub balance", T0.AddMinutes(7)).IsSuccess.ShouldBeTrue();

        context.EnergyOperations.AddRange(stake, confirmed, delegated, failedTopUp);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Search_orders_newest_first_and_maps_fields_exactly()
    {
        await using var context = NewContext();
        var directory = new EnergyOperationDirectory(context);

        var (items, total) = await directory.SearchAsync(new EnergyOperationAdminFilter(), page: 1, pageSize: 50, Ct);

        total.ShouldBe(4);
        items.Count.ShouldBe(4);
        items[0].CreatedAt.ShouldBeGreaterThan(items[^1].CreatedAt); // newest first

        var stakeRow = items.Single(i => i.OwnerAddress == "TStakeOwner");
        stakeRow.Kind.ShouldBe("Stake");
        stakeRow.Status.ShouldBe("Pending");
        stakeRow.AmountSunBaseUnits.ShouldBe(BigSun.ToString()); // exact, beyond Int64
        stakeRow.TargetAddress.ShouldBeNull();
    }

    [Fact]
    public async Task Filters_by_kind_and_status()
    {
        await using var context = NewContext();
        var directory = new EnergyOperationDirectory(context);

        var (delegates, _) = await directory.SearchAsync(
            new EnergyOperationAdminFilter(Kind: "Delegate"), 1, 50, Ct);
        delegates.ShouldHaveSingleItem().TargetAddress.ShouldBe("TDepositAddr");

        var (failed, _) = await directory.SearchAsync(
            new EnergyOperationAdminFilter(Status: "Failed"), 1, 50, Ct);
        failed.ShouldHaveSingleItem().Kind.ShouldBe("TopUp");
    }

    [Fact]
    public async Task Status_summary_counts_by_status()
    {
        await using var context = NewContext();
        var directory = new EnergyOperationDirectory(context);

        var counts = await directory.GetStatusCountsAsync(Chain.Tron, Ct);

        counts["Pending"].ShouldBe(2);   // the un-advanced stake + the delegate are both Pending
        counts["Confirmed"].ShouldBe(1);
        counts["Failed"].ShouldBe(1);
    }
}
