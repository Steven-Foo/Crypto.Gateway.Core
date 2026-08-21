using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Tests;

/// <summary>Direct coverage for <see cref="PaymentIntentDirectory.GetTotalsAsync"/> — the Ops
/// deposit-transactions screen's "expected amount" total, aggregated over the whole filtered set, not just
/// one page (§14).</summary>
public sealed class PaymentIntentDirectoryTests : IAsyncLifetime
{
    private const string DbName = "CpePaymentIntentDirectoryTests";
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private PaymentIntentDbContext _context = null!;
    private PaymentIntentDirectory _directory = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<PaymentIntentDbContext>()
            .UseSqlServer(ConnectionString).UseBigIntegerMoney().Options;
        _context = new PaymentIntentDbContext(options);
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.Database.EnsureCreatedAsync(Ct);
        _directory = new PaymentIntentDirectory(_context, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.DisposeAsync();
    }

    /// <summary>Each invoice gets its own fresh wallet/address by default — <c>UX_PaymentIntent_LiveWallet</c>
    /// (a filtered unique index, one live "Waiting" invoice per wallet) rejects two Waiting rows sharing a
    /// wallet, matching the real one-payment-per-address lock.</summary>
    private PaymentIntentEntity Persisted(string reference, string amount, Guid? assetId = null, Guid? walletId = null, string? address = null)
    {
        var now = DateTimeOffset.UtcNow;
        var intent = PaymentIntentEntity.Create(
            Merchant, reference, Chain.Tron, assetId ?? Asset, walletId ?? Guid.CreateVersion7(), address ?? $"TAddr-{reference}",
            BigInteger.Parse(amount), callbackUrl: null, now.AddMinutes(30), now.AddMinutes(35), now).Value;
        _context.PaymentIntents.Add(intent);
        return intent;
    }

    private static PaymentIntentAdminFilter FilterFor(Guid merchantId) =>
        new(merchantId, null, null, null, null, null, null, null);

    [Fact]
    public async Task GetTotalsAsync_sums_expected_amount_across_the_whole_filtered_set_not_just_one_page()
    {
        Persisted("TOTALS-1", "1000000");
        Persisted("TOTALS-2", "2500000");
        Persisted("TOTALS-3", "4000000");
        await _context.SaveChangesAsync(Ct);

        var totals = await _directory.GetTotalsAsync(FilterFor(Merchant), Ct);

        BigInteger.Parse(totals.TotalExpectedAmountBaseUnits).ShouldBe(BigInteger.Parse("7500000"));
        totals.DistinctAssetCount.ShouldBe(1);
        totals.MatchedDepositIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTotalsAsync_collects_every_non_null_matched_deposit_id_in_the_filtered_set()
    {
        var matched = Persisted("MATCHED-1", "1000000");
        var depositId = Guid.CreateVersion7();
        matched.MatchTo(depositId, "0xtxhash", BigInteger.Parse("1000000"), DateTimeOffset.UtcNow);
        Persisted("UNMATCHED-1", "2000000"); // still Waiting — no MatchedDepositId
        await _context.SaveChangesAsync(Ct);

        var totals = await _directory.GetTotalsAsync(FilterFor(Merchant), Ct);

        totals.MatchedDepositIds.ShouldBe([depositId]);
    }

    [Fact]
    public async Task GetTotalsAsync_flags_more_than_one_asset_in_the_filtered_set()
    {
        var otherAsset = Guid.CreateVersion7();
        Persisted("MIX-1", "1000000", assetId: Asset);
        Persisted("MIX-2", "2000000", assetId: otherAsset);
        await _context.SaveChangesAsync(Ct);

        var totals = await _directory.GetTotalsAsync(FilterFor(Merchant), Ct);

        totals.DistinctAssetCount.ShouldBe(2);
        // The raw sum is still returned (caller's choice to combine or not) — never silently dropped.
        BigInteger.Parse(totals.TotalExpectedAmountBaseUnits).ShouldBe(BigInteger.Parse("3000000"));
    }

    [Fact]
    public async Task GetTotalsAsync_on_an_empty_result_set_sums_to_zero()
    {
        var totals = await _directory.GetTotalsAsync(FilterFor(Guid.CreateVersion7()), Ct);

        totals.TotalExpectedAmountBaseUnits.ShouldBe("0");
        totals.DistinctAssetCount.ShouldBe(0);
        totals.MatchedDepositIds.ShouldBeEmpty();
    }
}
