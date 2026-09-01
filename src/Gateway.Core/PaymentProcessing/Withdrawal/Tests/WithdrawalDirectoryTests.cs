using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;
using CryptoPaymentEngine.Infrastructure.Persistence.Money;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

/// <summary>
/// The merchant transaction-query directory lookup, disambiguated by withdrawal kind. A user payout and a
/// merchant cash-out can share one reference (the idempotency key is <c>(merchant, kind, reference)</c>), so
/// <see cref="WithdrawalDirectory.FindByMerchantReferenceAsync"/> must return the record matching the requested
/// kind — never the wrong kind, which would leak one flow's transaction into the other.
/// </summary>
public sealed class WithdrawalDirectoryTests : IAsyncLifetime
{
    private const string DbName = "CpeWithdrawalDirectoryTests";
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Guid Asset = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private WithdrawalDbContext _context = null!;
    private WithdrawalDirectory _directory = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<WithdrawalDbContext>()
            .UseSqlServer(ConnectionString).UseBigIntegerMoney().Options;
        _context = new WithdrawalDbContext(options);
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.Database.EnsureCreatedAsync(Ct);
        _directory = new WithdrawalDirectory(_context);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync(Ct);
        await _context.DisposeAsync();
    }

    private WithdrawalEntity Persisted(string reference, WithdrawalKind kind, string destination, string amount)
    {
        var withdrawal = WithdrawalEntity.Request(
            Merchant, Asset, Chain.Tron, destination, BigInteger.Parse(amount), BigInteger.Parse("10000"),
            reference, callbackUrl: null, DateTimeOffset.UtcNow, kind).Value;
        _context.Withdrawals.Add(withdrawal);
        return withdrawal;
    }

    [Fact]
    public async Task Lookup_returns_the_record_matching_the_requested_kind_when_a_reference_is_shared()
    {
        const string reference = "ORDER-SHARED";
        var payout = Persisted(reference, WithdrawalKind.User, "TUserDest", "1000000");
        var cashOut = Persisted(reference, WithdrawalKind.Merchant, "TMerchantDest", "2000000");
        await _context.SaveChangesAsync(Ct);

        var foundUser = await _directory.FindByMerchantReferenceAsync(Merchant, reference, "User", Ct);
        var foundMerchant = await _directory.FindByMerchantReferenceAsync(Merchant, reference, "Merchant", Ct);

        foundUser.ShouldNotBeNull();
        foundUser!.WithdrawalId.ShouldBe(payout.Id);
        foundUser.DestinationAddress.ShouldBe("TUserDest");

        foundMerchant.ShouldNotBeNull();
        foundMerchant!.WithdrawalId.ShouldBe(cashOut.Id);
        foundMerchant.DestinationAddress.ShouldBe("TMerchantDest");
    }

    [Fact]
    public async Task Default_kind_is_user()
    {
        const string reference = "ORDER-DEFAULT";
        var payout = Persisted(reference, WithdrawalKind.User, "TUserDest", "1000000");
        await _context.SaveChangesAsync(Ct);

        var found = await _directory.FindByMerchantReferenceAsync(Merchant, reference, cancellationToken: Ct);

        found.ShouldNotBeNull();
        found!.WithdrawalId.ShouldBe(payout.Id);
    }

    [Fact]
    public async Task Merchant_kind_does_not_match_a_user_only_reference()
    {
        const string reference = "ORDER-USER-ONLY";
        Persisted(reference, WithdrawalKind.User, "TUserDest", "1000000");
        await _context.SaveChangesAsync(Ct);

        var found = await _directory.FindByMerchantReferenceAsync(Merchant, reference, "Merchant", Ct);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task GetTotalsAsync_sums_across_the_whole_filtered_set_not_just_one_page()
    {
        Persisted("TOTALS-1", WithdrawalKind.User, "TDest1", "1000000");
        Persisted("TOTALS-2", WithdrawalKind.User, "TDest2", "2500000");
        Persisted("TOTALS-3", WithdrawalKind.User, "TDest3", "4000000");
        await _context.SaveChangesAsync(Ct);

        var filter = new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null);
        var totals = await _directory.GetTotalsAsync(filter, Ct);

        // 1,000,000 + 2,500,000 + 4,000,000; fee is 10,000 per row (3 rows) from the Persisted helper.
        BigInteger.Parse(totals.TotalAmountBaseUnits).ShouldBe(BigInteger.Parse("7500000"));
        BigInteger.Parse(totals.TotalFeeBaseUnits).ShouldBe(BigInteger.Parse("30000"));
        totals.DistinctAssetCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetTotalsAsync_respects_the_kind_filter()
    {
        Persisted("KIND-USER", WithdrawalKind.User, "TUserDest", "1000000");
        Persisted("KIND-CASHOUT", WithdrawalKind.Merchant, "TMerchantDest", "9000000");
        await _context.SaveChangesAsync(Ct);

        var userOnly = new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null, "User");
        var totals = await _directory.GetTotalsAsync(userOnly, Ct);

        BigInteger.Parse(totals.TotalAmountBaseUnits).ShouldBe(BigInteger.Parse("1000000"));
    }

    [Fact]
    public async Task GetTotalsAsync_flags_more_than_one_asset_in_the_filtered_set()
    {
        var otherAsset = Guid.CreateVersion7();
        var first = WithdrawalEntity.Request(
            Merchant, Asset, Chain.Tron, "TDest1", BigInteger.Parse("1000000"), BigInteger.Parse("10000"),
            "MIX-1", callbackUrl: null, DateTimeOffset.UtcNow, WithdrawalKind.User).Value;
        var second = WithdrawalEntity.Request(
            Merchant, otherAsset, Chain.Tron, "TDest2", BigInteger.Parse("2000000"), BigInteger.Parse("10000"),
            "MIX-2", callbackUrl: null, DateTimeOffset.UtcNow, WithdrawalKind.User).Value;
        _context.Withdrawals.AddRange(first, second);
        await _context.SaveChangesAsync(Ct);

        var filter = new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null);
        var totals = await _directory.GetTotalsAsync(filter, Ct);

        totals.DistinctAssetCount.ShouldBe(2);
        // The raw sum is still returned (caller's choice to combine or not) — never silently dropped.
        BigInteger.Parse(totals.TotalAmountBaseUnits).ShouldBe(BigInteger.Parse("3000000"));
    }

    [Fact]
    public async Task GetTotalsAsync_on_an_empty_result_set_sums_to_zero()
    {
        var filter = new WithdrawalAdminFilter(Guid.CreateVersion7(), null, null, null, null, null, null, null);
        var totals = await _directory.GetTotalsAsync(filter, Ct);

        totals.TotalAmountBaseUnits.ShouldBe("0");
        totals.TotalFeeBaseUnits.ShouldBe("0");
        totals.DistinctAssetCount.ShouldBe(0);
    }

    // ── effective-status filter (REQ-15) ───────────────────────────────────────────────────────────────

    /// <summary>Builds one withdrawal in each reachable state, so every bucket of the vocabulary is covered.</summary>
    private async Task SeedOnePerStatusAsync()
    {
        var now = DateTimeOffset.UtcNow;

        // "pending" — the whole pre-confirm pipeline collapses here. Two of them, so a filter that returned
        // only one domain status would fail.
        Persisted("ST-RESERVING", WithdrawalKind.User, "TDest", "1000000");
        var approved = Persisted("ST-APPROVED", WithdrawalKind.User, "TDest", "1000000");
        approved.ConfirmReserved(requiresApproval: false, now);

        var pendingPlatform = Persisted("ST-PENDING-APPROVAL", WithdrawalKind.User, "TDest", "1000000");
        pendingPlatform.ConfirmReserved(requiresApproval: true, now);

        var pendingMerchant = Persisted("ST-PENDING-MERCHANT", WithdrawalKind.User, "TDest", "1000000");
        pendingMerchant.ConfirmReserved(requiresApproval: false, now, requiresMerchantApproval: true);

        var awaitingFunds = Persisted("ST-AWAITING-FUNDS", WithdrawalKind.User, "TDest", "1000000");
        awaitingFunds.ConfirmReserved(requiresApproval: false, now);
        awaitingFunds.Park("hot wallet short", now);

        var awaitingRelease = Persisted("ST-AWAITING-RELEASE", WithdrawalKind.User, "TDest", "1000000");
        awaitingRelease.ConfirmReserved(requiresApproval: false, now);
        awaitingRelease.MarkAwaitingRelease("large payout", now);

        var confirmed = Persisted("ST-CONFIRMED", WithdrawalKind.User, "TDest", "1000000");
        confirmed.ConfirmReserved(requiresApproval: false, now);
        confirmed.RecordSigned(Guid.CreateVersion7(), Guid.CreateVersion7(), [1, 2, 3], now);
        confirmed.MarkBroadcast("0xhash", now);
        confirmed.Confirm(now);

        // "failed" covers BOTH Rejected and Failed — the bucket a one-to-one mapping would get wrong.
        var rejected = Persisted("ST-REJECTED", WithdrawalKind.User, "TDest", "1000000");
        rejected.ConfirmReserved(requiresApproval: true, now);
        rejected.Reject("staff", "not allowed", now);

        var failed = Persisted("ST-FAILED", WithdrawalKind.User, "TDest", "1000000");
        failed.ConfirmReserved(requiresApproval: false, now);
        failed.Fail("build error", now);

        // The merchant-settlement queue: this system records these rather than paying them, so they have their
        // own three states. Seeded as Merchant-kind because that is the only kind that reaches them.
        var awaitingAudit = Persisted("ST-AUDIT", WithdrawalKind.Merchant, "TSettle", "1000000");
        awaitingAudit.ConfirmReserved(requiresApproval: false, now);

        var awaitingFinance = Persisted("ST-FINANCE", WithdrawalKind.Merchant, "TSettle", "1000000");
        awaitingFinance.ConfirmReserved(requiresApproval: false, now);
        awaitingFinance.AdminAuditApprove("admin", now);

        var settled = Persisted("ST-SETTLED", WithdrawalKind.Merchant, "TSettle", "1000000");
        settled.ConfirmReserved(requiresApproval: false, now);
        settled.AdminAuditApprove("admin", now);
        settled.RecordFinanceSettlement("finance", "0xsettled", "TCompanyWallet", now);

        await _context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// The load-bearing test: for EVERY status in the published vocabulary, filtering by it returns exactly
    /// the rows the unfiltered search labels with that same status. This is what stops the SQL filter and the
    /// projection drifting apart — a drift would hide work from an operator behind a filter they believe is
    /// applied (the settlement queue that motivated REQ-15).
    /// </summary>
    [Fact]
    public async Task Filtering_by_each_effective_status_returns_exactly_the_rows_reported_with_it()
    {
        await SeedOnePerStatusAsync();

        var unfiltered = new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null);
        var (allRows, _) = await _directory.SearchAsync(unfiltered, 1, 100, Ct);

        // Guard the guard: if a state became unreachable the loop below would trivially pass on empty sets.
        allRows.Select(r => r.Status).Distinct().Count()
            .ShouldBe(WithdrawalEffectiveStatuses.All.Length);

        foreach (var status in WithdrawalEffectiveStatuses.All)
        {
            var expected = allRows.Where(r => r.Status == status)
                .Select(r => r.WithdrawalId).OrderBy(id => id).ToList();
            var (filtered, totalCount) = await _directory.SearchAsync(unfiltered with { Status = status }, 1, 100, Ct);

            filtered.Select(r => r.WithdrawalId).OrderBy(id => id).ToList().ShouldBe(expected, $"status '{status}'");
            // The count is what pages the queue — it must reflect the filter, not the unfiltered set.
            totalCount.ShouldBe(expected.Count, $"status '{status}'");
            filtered.ShouldAllBe(r => r.Status == status);
        }
    }

    [Fact]
    public async Task The_pending_bucket_collapses_the_whole_pre_confirm_pipeline()
    {
        await SeedOnePerStatusAsync();

        var filter = new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null, Status: "pending");
        var (rows, _) = await _directory.SearchAsync(filter, 1, 100, Ct);

        // Reserving and Approved both report "pending"; the holds and the two approval gates do NOT.
        rows.Select(r => r.MerchantTransactionId).OrderBy(r => r)
            .ShouldBe(["ST-APPROVED", "ST-RESERVING"]);
    }

    [Fact]
    public async Task An_unknown_status_matches_nothing_rather_than_everything()
    {
        await SeedOnePerStatusAsync();

        // The host rejects this with a 400 first; this is the second line. Returning the unfiltered set here
        // would show an operator every payout while they believe one queue is selected.
        var filter = new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null, Status: "not-a-status");
        var (rows, totalCount) = await _directory.SearchAsync(filter, 1, 100, Ct);

        rows.ShouldBeEmpty();
        totalCount.ShouldBe(0);
    }

    /// <summary>
    /// The dashboard work-queue counts (REQ-3). These must agree exactly with what the list endpoint reports
    /// for the same status — a tile saying "3 awaiting approval" that opens a list of 5 is the bug this
    /// guards. Asserted against the filtered search rather than hardcoded numbers, so the two cannot diverge.
    /// </summary>
    [Fact]
    public async Task Status_counts_agree_with_the_filtered_search_for_every_status()
    {
        await SeedOnePerStatusAsync();

        var counts = await _directory.GetStatusCountsAsync(Ct);

        // Every status in the vocabulary is present as a key, including the zero ones — a dashboard should
        // render "0", not omit the tile because nothing is queued.
        counts.Keys.OrderBy(k => k).ToList()
            .ShouldBe(WithdrawalEffectiveStatuses.All.OrderBy(k => k).ToList());

        foreach (var status in WithdrawalEffectiveStatuses.All)
        {
            var (_, searchTotal) = await _directory.SearchAsync(
                new WithdrawalAdminFilter(Merchant, null, null, null, null, null, null, null, Status: status), 1, 1, Ct);

            counts[status].ShouldBe(searchTotal, $"status '{status}'");
        }
    }

    /// <summary>"pending" and "failed" each fold several domain statuses, so the count must SUM them, not
    /// report whichever one it saw last.</summary>
    [Fact]
    public async Task Status_counts_sum_the_folded_buckets()
    {
        await SeedOnePerStatusAsync();

        var counts = await _directory.GetStatusCountsAsync(Ct);

        counts["pending"].ShouldBe(2);   // Reserving + Approved
        counts["failed"].ShouldBe(2);    // Rejected + Failed
        counts["confirmed"].ShouldBe(1);
    }

    [Fact]
    public async Task The_status_filter_combines_with_kind_rather_than_replacing_it()
    {
        var now = DateTimeOffset.UtcNow;
        var userPayout = Persisted("K-USER", WithdrawalKind.User, "TDest", "1000000");
        userPayout.ConfirmReserved(requiresApproval: true, now);
        var cashOut = Persisted("K-MERCHANT", WithdrawalKind.Merchant, "TDest", "1000000");
        cashOut.ConfirmReserved(requiresApproval: true, now);
        await _context.SaveChangesAsync(Ct);

        var filter = new WithdrawalAdminFilter(
            Merchant, null, null, null, null, null, null, null, Kind: "Merchant", Status: "pending_approval");
        var (rows, _) = await _directory.SearchAsync(filter, 1, 100, Ct);

        rows.Select(r => r.MerchantTransactionId).ShouldBe(["K-MERCHANT"]);
    }
}
