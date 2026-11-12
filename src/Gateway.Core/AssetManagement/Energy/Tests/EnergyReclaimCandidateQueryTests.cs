using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// The take-back search on real SQL Server — the one place a LINQ query could fail to translate or silently
/// pick the wrong addresses. Each target address below is one rule.
/// </summary>
public sealed class EnergyReclaimCandidateQueryTests : IAsyncLifetime
{
    private const string DbName = "CpeEnergyReclaimQueryTests";
    private const string Hub = "THub";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Staking = Guid.CreateVersion7();
    private static readonly BigInteger Sun = new(13_000_000_000);

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
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    private static EnergyOperation ConfirmedDelegate(string target, DateTimeOffset at)
    {
        var op = EnergyOperation.CreateDelegate(Staking, Chain.Tron, Hub, target, Sun, at.AddMinutes(-2)).Value;
        op.RecordSigned(Guid.CreateVersion7(), [1], at.AddMinutes(-1)).IsSuccess.ShouldBeTrue();
        op.MarkBroadcast("tx-" + Guid.NewGuid().ToString("N"), at.AddMinutes(-1)).IsSuccess.ShouldBeTrue();
        op.Confirm(at).IsSuccess.ShouldBeTrue();
        return op;
    }

    [Fact]
    public async Task Picks_only_addresses_that_are_due_a_take_back()
    {
        var cutoff = T0.AddHours(1);
        await using (var seed = NewContext())
        {
            seed.EnergyOperations.AddRange(
                // DUE: lent long ago, never taken back.
                ConfirmedDelegate("TDue", T0),
                // NOT DUE: lent too recently.
                ConfirmedDelegate("TRecent", cutoff.AddMinutes(10)),
                // NOT DUE: already taken back after the lend.
                ConfirmedDelegate("TReclaimed", T0),
                EnergyOperation.CreateUndelegate(Staking, Chain.Tron, Hub, "TReclaimed", Sun, T0.AddMinutes(5)).Value,
                // DUE AGAIN: taken back, then lent again later (the take-back predates the new lend).
                ConfirmedDelegate("TRelent", T0.AddMinutes(20)),
                EnergyOperation.CreateUndelegate(Staking, Chain.Tron, Hub, "TRelent", Sun, T0.AddMinutes(10)).Value,
                // NOT DUE: a new lend is in progress.
                ConfirmedDelegate("TBusy", T0),
                EnergyOperation.CreateDelegate(Staking, Chain.Tron, Hub, "TBusy", Sun, T0.AddMinutes(30)).Value,
                // NOT DUE: a platform wallet (excluded by the caller).
                ConfirmedDelegate("THot", T0),
                // NOT DUE: another staking wallet's lend.
                ConfirmedDelegate("TOtherHub", T0) is var other ? Reassign(other) : null!,
                // NOTHING-LENT marker counts as a finished take-back.
                ConfirmedDelegate("TMarked", T0),
                EnergyOperation.RecordNothingToReclaim(Staking, Chain.Tron, Hub, "TMarked", T0.AddMinutes(5)).Value);

            // DUE AGAIN: the previous take-back FAILED, so the energy is still out.
            seed.EnergyOperations.Add(ConfirmedDelegate("TFailedTakeBack", T0));
            var failed = EnergyOperation.CreateUndelegate(Staking, Chain.Tron, Hub, "TFailedTakeBack", Sun, T0.AddMinutes(5)).Value;
            failed.Fail("node refused", T0.AddMinutes(6)).IsSuccess.ShouldBeTrue();
            seed.EnergyOperations.Add(failed);

            await seed.SaveChangesAsync(Ct);
        }

        await using var context = NewContext();
        var found = await new EnergyOperationRepository(context).ListReclaimCandidatesAsync(
            Chain.Tron, Staking, cutoff, ["THot"], 50, Ct);

        found.ShouldBe(["TDue", "TFailedTakeBack", "TRelent"], ignoreOrder: true);
    }

    [Fact]
    public async Task Respects_the_batch_size_oldest_first()
    {
        await using (var seed = NewContext())
        {
            seed.EnergyOperations.AddRange(
                ConfirmedDelegate("TOldest", T0),
                ConfirmedDelegate("TMiddle", T0.AddMinutes(1)),
                ConfirmedDelegate("TNewest", T0.AddMinutes(2)));
            await seed.SaveChangesAsync(Ct);
        }

        await using var context = NewContext();
        var found = await new EnergyOperationRepository(context).ListReclaimCandidatesAsync(
            Chain.Tron, Staking, T0.AddHours(1), [], 2, Ct);

        found.ShouldBe(["TOldest", "TMiddle"]);
    }

    private static EnergyOperation Reassign(EnergyOperation _) =>
        ConfirmedDelegateFor(Guid.CreateVersion7(), "TOtherHub", T0);

    private static EnergyOperation ConfirmedDelegateFor(Guid stakingWalletId, string target, DateTimeOffset at)
    {
        var op = EnergyOperation.CreateDelegate(stakingWalletId, Chain.Tron, "TOtherHubOwner", target, Sun, at.AddMinutes(-2)).Value;
        op.RecordSigned(Guid.CreateVersion7(), [1], at.AddMinutes(-1)).IsSuccess.ShouldBeTrue();
        op.MarkBroadcast("tx-" + Guid.NewGuid().ToString("N"), at.AddMinutes(-1)).IsSuccess.ShouldBeTrue();
        op.Confirm(at).IsSuccess.ShouldBeTrue();
        return op;
    }
}
