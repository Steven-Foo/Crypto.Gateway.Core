using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Tests;

/// <summary>Direct coverage for <see cref="DepositLookup.SumByIdsAsync"/> — the Ops deposit-transactions
/// screen's "actual received amount"/fee totals, aggregated over the whole filtered set (§14). Deliberately
/// its own database (not <see cref="DepositTestHost"/>'s shared "CpeDepositTests") — that name is hardcoded
/// with no override point, and xUnit runs test classes in parallel by default, so a second class reusing it
/// races <see cref="DepositPersistenceTests"/>'s own EnsureDeleted/EnsureCreated.</summary>
public sealed class DepositLookupTests : IAsyncLifetime
{
    private const string DbName = "CpeDepositLookupTests";
    private static readonly Guid WalletId = Guid.CreateVersion7();
    private static readonly Guid MerchantId = Guid.CreateVersion7();
    private static readonly Guid AssetId = Guid.CreateVersion7();
    private static readonly DepositPolicy Policy = new(CreditStrategy.Confirmations, 3, BigInteger.Parse("1000"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static DepositDbContext Context() =>
        new(new DbContextOptionsBuilder<DepositDbContext>().UseSqlServer(ConnectionString).UseBigIntegerMoney().Options);

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

    private static DepositEntity Recorded(string txHash, string amount, string fee) =>
        DepositEntity.Record(
            Chain.Tron, "TWatchedAddr", WalletId, MerchantId, AssetId,
            BigInteger.Parse(amount), BigInteger.Parse(fee), txHash, outputIndex: 0,
            blockNumber: 100, blockHash: "h100", Policy, DateTimeOffset.UtcNow).Value;

    [Fact]
    public async Task SumByIdsAsync_sums_amount_and_fee_across_the_given_ids()
    {
        var one = Recorded("0xtx1", "1000000", "10000");
        var two = Recorded("0xtx2", "2500000", "25000");
        await using (var ctx = Context())
        {
            ctx.Deposits.AddRange(one, two);
            await ctx.SaveChangesAsync(Ct);
        }

        await using var verify = Context();
        var lookup = new DepositLookup(verify);
        var totals = await lookup.SumByIdsAsync([one.Id, two.Id], Ct);

        BigInteger.Parse(totals.TotalAmountBaseUnits).ShouldBe(BigInteger.Parse("3500000"));
        BigInteger.Parse(totals.TotalFeeBaseUnits).ShouldBe(BigInteger.Parse("35000"));
    }

    [Fact]
    public async Task SumByIdsAsync_ignores_ids_outside_the_given_set()
    {
        var included = Recorded("0xtx3", "1000000", "10000");
        var excluded = Recorded("0xtx4", "9000000", "90000");
        await using (var ctx = Context())
        {
            ctx.Deposits.AddRange(included, excluded);
            await ctx.SaveChangesAsync(Ct);
        }

        await using var verify = Context();
        var lookup = new DepositLookup(verify);
        var totals = await lookup.SumByIdsAsync([included.Id], Ct);

        BigInteger.Parse(totals.TotalAmountBaseUnits).ShouldBe(BigInteger.Parse("1000000"));
    }

    [Fact]
    public async Task SumByIdsAsync_on_an_empty_id_set_sums_to_zero_without_querying()
    {
        await using var verify = Context();
        var lookup = new DepositLookup(verify);
        var totals = await lookup.SumByIdsAsync([], Ct);

        totals.TotalAmountBaseUnits.ShouldBe("0");
        totals.TotalFeeBaseUnits.ShouldBe("0");
    }
}
