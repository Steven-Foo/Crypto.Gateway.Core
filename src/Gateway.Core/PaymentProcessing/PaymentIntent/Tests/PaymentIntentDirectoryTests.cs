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

    // ── effective-status filter (REQ-15) ───────────────────────────────────────────────────────────────

    /// <summary>A clock offset from the real one, so a still-Waiting invoice can be observed after its
    /// expiry without waiting 30 minutes. "expired" is the one effective status that is time-derived rather
    /// than stored, which is exactly why it needs its own coverage.</summary>
    private sealed class ShiftedTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow().Add(offset);
    }

    private async Task SeedOnePerStatusAsync()
    {
        var now = DateTimeOffset.UtcNow;

        Persisted("PI-WAITING", "1000000");

        var matched = Persisted("PI-MATCHED", "1000000");
        matched.MatchTo(Guid.CreateVersion7(), "0xtxhash", BigInteger.Parse("1000000"), now);

        var expired = Persisted("PI-EXPIRED", "1000000");
        expired.Expire(now);

        var failed = Persisted("PI-FAILED", "1000000");
        failed.Fail("cancelled by ops", now);

        await _context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// The load-bearing test, matching the withdrawal side: filtering by each published status returns
    /// exactly the rows the unfiltered search labels with it. A filter that disagreed with the projection
    /// would hide invoices behind a filter the operator believes is applied.
    /// </summary>
    [Fact]
    public async Task Filtering_by_each_effective_status_returns_exactly_the_rows_reported_with_it()
    {
        await SeedOnePerStatusAsync();

        var unfiltered = FilterFor(Merchant);
        var (allRows, _) = await _directory.SearchAsync(unfiltered, 1, 100, Ct);

        allRows.Select(r => r.Status).Distinct().Count()
            .ShouldBe(PaymentIntentEffectiveStatuses.All.Length);

        foreach (var status in PaymentIntentEffectiveStatuses.All)
        {
            var expected = allRows.Where(r => r.Status == status)
                .Select(r => r.MerchantTransactionId).OrderBy(r => r).ToList();
            var (filtered, totalCount) = await _directory.SearchAsync(unfiltered with { Status = status }, 1, 100, Ct);

            filtered.Select(r => r.MerchantTransactionId).OrderBy(r => r).ToList().ShouldBe(expected, $"status '{status}'");
            totalCount.ShouldBe(expected.Count, $"status '{status}'");
        }
    }

    /// <summary>
    /// The money-relevant subtlety: a lapsed-but-not-yet-swept invoice is still <c>Waiting</c> in the
    /// database but already reads "expired" to payer, merchant and Ops. The filter evaluates the same clock
    /// the projection does, so it must move with it — searching "expired" finds it, "pending" no longer does.
    /// </summary>
    [Fact]
    public async Task A_lapsed_but_unswept_invoice_filters_as_expired_not_pending()
    {
        Persisted("PI-LAPSING", "1000000"); // expires 30 minutes out
        await _context.SaveChangesAsync(Ct);

        var beforeExpiry = FilterFor(Merchant) with { Status = "pending" };
        var (pendingNow, _) = await _directory.SearchAsync(beforeExpiry, 1, 100, Ct);
        pendingNow.Select(r => r.MerchantTransactionId).ShouldBe(["PI-LAPSING"]);

        // Same untouched row, same stored status — only the clock moved.
        var future = new PaymentIntentDirectory(_context, new ShiftedTimeProvider(TimeSpan.FromHours(1)));

        var (pendingLater, _) = await future.SearchAsync(beforeExpiry, 1, 100, Ct);
        pendingLater.ShouldBeEmpty();

        var (expiredLater, _) = await future.SearchAsync(FilterFor(Merchant) with { Status = "expired" }, 1, 100, Ct);
        expiredLater.Select(r => r.MerchantTransactionId).ShouldBe(["PI-LAPSING"]);
        expiredLater.Single().Status.ShouldBe("expired"); // the filter and the label agree
    }

    [Fact]
    public async Task An_unknown_status_matches_nothing_rather_than_everything()
    {
        await SeedOnePerStatusAsync();

        var (rows, totalCount) = await _directory.SearchAsync(
            FilterFor(Merchant) with { Status = "not-a-status" }, 1, 100, Ct);

        rows.ShouldBeEmpty();
        totalCount.ShouldBe(0);
    }
}
