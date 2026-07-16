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
/// EnergyPolicy on real SQL Server: proves the map, the decimal(38,0) round-trip for BigInteger thresholds
/// (values well beyond Int64), and the one-policy-per-(chain, wallet type) unique index.
/// </summary>
public sealed class EnergyPolicyPersistenceTests : IAsyncLifetime
{
    private const string DbName = "CpeEnergyPolicyTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact]
    public async Task A_policy_round_trips_with_exact_big_integer_thresholds()
    {
        // Beyond Int64, to prove decimal(38,0), not a silently-capped converter (§7.2).
        var minimum = BigInteger.Parse("12345678901234567890");
        var target = BigInteger.Parse("98765432109876543210");

        await using (var context = NewContext())
        {
            context.EnergyPolicies.Add(EnergyPolicy.Create(
                Chain.Tron, "HotWithdrawal", minimum, target, minimum, minimum, true, false).Value);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            var policy = await context.EnergyPolicies.SingleAsync(Ct);
            policy.MinimumEnergy.ShouldBe(minimum);
            policy.TargetEnergy.ShouldBe(target);
            policy.EnableAutoStake.ShouldBeTrue();
            policy.WalletType.ShouldBe("HotWithdrawal");
        }
    }

    [Fact]
    public async Task Two_policies_for_the_same_chain_and_wallet_type_are_rejected()
    {
        await using (var context = NewContext())
        {
            context.EnergyPolicies.Add(EnergyPolicy.Create(Chain.Tron, "HotWithdrawal", 1, 2, 2, 2, false, false).Value);
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = NewContext())
        {
            context.EnergyPolicies.Add(EnergyPolicy.Create(Chain.Tron, "HotWithdrawal", 3, 4, 4, 4, false, false).Value);
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        }
    }
}
