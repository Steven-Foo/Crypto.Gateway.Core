using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using SweepEntity = CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain.Sweep;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Tests;

/// <summary>
/// The Ops read directory over sweeps, on real SQL Server. Proves the no-tracking search maps the state
/// machine (status name, exact amount beyond Int64 — decimal(38,0), §14), orders newest-first, filters by
/// chain/status/wallet, and summarises counts by status.
/// </summary>
public sealed class SweepDirectoryTests : IAsyncLifetime
{
    private const string DbName = "CpeSweepDirectoryTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Asset = Guid.CreateVersion7();

    // Beyond Int64, to prove decimal(38,0), not a silently-capped converter (§7.2/§14).
    private static readonly BigInteger BigAmount = BigInteger.Parse("12345678901234567890");

    private static readonly Guid PendingWallet = Guid.CreateVersion7();

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static SweepDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SweepDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

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

    private async Task SeedAsync()
    {
        await using var context = NewContext();

        // Distinct wallets so the one-in-flight-per-(wallet, asset) unique index never collides.
        var pending = SweepEntity.Create(PendingWallet, Chain.Tron, Asset, "TFrom1", "TCold", BigAmount, T0).Value;

        var confirmed = SweepEntity.Create(Guid.CreateVersion7(), Chain.Tron, Asset, "TFrom2", "TCold", new BigInteger(5_000_000), T0.AddMinutes(1)).Value;
        confirmed.RecordSigned(Guid.CreateVersion7(), [1], T0.AddMinutes(2)).IsSuccess.ShouldBeTrue();
        confirmed.MarkBroadcast("tx-confirmed", T0.AddMinutes(3)).IsSuccess.ShouldBeTrue();
        confirmed.Confirm(T0.AddMinutes(4)).IsSuccess.ShouldBeTrue();

        var failed = SweepEntity.Create(Guid.CreateVersion7(), Chain.Tron, Asset, "TFrom3", "TCold", new BigInteger(2_000_000), T0.AddMinutes(5)).Value;
        failed.Fail("no energy", T0.AddMinutes(6)).IsSuccess.ShouldBeTrue();

        context.Sweeps.AddRange(pending, confirmed, failed);
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Search_orders_newest_first_and_maps_fields_exactly()
    {
        await using var context = NewContext();
        var directory = new SweepDirectory(context);

        var (items, total) = await directory.SearchAsync(new SweepAdminFilter(), page: 1, pageSize: 50, Ct);

        total.ShouldBe(3);
        items[0].CreatedAt.ShouldBeGreaterThan(items[^1].CreatedAt); // newest first

        var pendingRow = items.Single(i => i.WalletId == PendingWallet);
        pendingRow.Status.ShouldBe("Pending");
        pendingRow.ToAddress.ShouldBe("TCold");
        pendingRow.AmountBaseUnits.ShouldBe(BigAmount.ToString()); // exact, beyond Int64
    }

    [Fact]
    public async Task Filters_by_status_and_wallet()
    {
        await using var context = NewContext();
        var directory = new SweepDirectory(context);

        var (failed, _) = await directory.SearchAsync(new SweepAdminFilter(Status: "Failed"), 1, 50, Ct);
        failed.ShouldHaveSingleItem().FailureReason.ShouldBe("no energy");

        var (byWallet, _) = await directory.SearchAsync(new SweepAdminFilter(WalletId: PendingWallet), 1, 50, Ct);
        byWallet.ShouldHaveSingleItem().Status.ShouldBe("Pending");
    }

    [Fact]
    public async Task Status_summary_counts_by_status()
    {
        await using var context = NewContext();
        var directory = new SweepDirectory(context);

        var counts = await directory.GetStatusCountsAsync(Chain.Tron, Ct);

        counts["Pending"].ShouldBe(1);
        counts["Confirmed"].ShouldBe(1);
        counts["Failed"].ShouldBe(1);
    }
}
