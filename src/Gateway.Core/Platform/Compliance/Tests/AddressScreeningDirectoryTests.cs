using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Tests;

/// <summary>
/// The Ops read directory over the screening evidence trail, on real SQL Server. The trail is append-only,
/// so the load-bearing property here is that the read never confuses an address's HISTORY with its CURRENT
/// standing: the list shows every row, while the counters show each address once at its latest verdict.
/// </summary>
public sealed class AddressScreeningDirectoryTests : IAsyncLifetime
{
    private const string DbName = "CpeComplianceDirectoryTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Clean = "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e";
    private const string Flagged = "TEvwn7VF4KWfWfUSKipy6mFgjxPREPwFk2";
    private const string Rehabilitated = "TQp6K2nHpqdk5d6q8VjAYLvp1ufUKVpsts";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static ComplianceDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ComplianceDbContext>().UseSqlServer(ConnectionString).Options);

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

    private static async Task SeedAsync()
    {
        await using var context = NewContext();

        context.AddressScreenings.AddRange(
            AddressScreening.Completed(
                Chain.Tron, Clean, ScreeningPurpose.PayoutDestination, "MistTrack", ScreeningDecision.Allow,
                3, "Low", ["sanctioned_entity", "illicit_activity"], addressLabel: null,
                reportUrl: "https://files.misttrack.io/riskReport/clean",
                rawResponse: "{\"success\":true,\"data\":{\"score\":3}}",
                policyDescription: "block>=91;review>=71;always_block_direct_only=sanctioned_entity",
                screenedAt: T0, freshFor: TimeSpan.FromDays(30)),

            AddressScreening.Completed(
                Chain.Tron, Flagged, ScreeningPurpose.SettlementWallet, "MistTrack", ScreeningDecision.Block,
                100, "Severe", ["Malicious Address"], addressLabel: "hyperunit.xyz", reportUrl: null,
                rawResponse: "{\"success\":true,\"data\":{\"score\":100}}",
                policyDescription: "block>=91;review>=71;always_block_direct_only=sanctioned_entity",
                screenedAt: T0.AddHours(2), freshFor: TimeSpan.FromDays(30)),

            // Screened twice. The older verdict was a Block; the newer one is an Allow. This is what makes
            // "count rows" and "count addresses" give different answers.
            AddressScreening.Completed(
                Chain.Tron, Rehabilitated, ScreeningPurpose.PayoutDestination, "MistTrack",
                ScreeningDecision.Block, 95, "Severe", ["Mixer"], addressLabel: null, reportUrl: null,
                rawResponse: null,
                policyDescription: "block>=91;review>=71;always_block_direct_only=sanctioned_entity",
                screenedAt: T0.AddHours(1), freshFor: TimeSpan.FromDays(30)),

            AddressScreening.Completed(
                Chain.Tron, Rehabilitated, ScreeningPurpose.PayoutDestination, "MistTrack",
                ScreeningDecision.Allow, 20, "Low", [], addressLabel: null, reportUrl: null,
                rawResponse: null,
                policyDescription: "block>=91;review>=71;always_block_direct_only=sanctioned_entity",
                screenedAt: T0.AddHours(3), freshFor: TimeSpan.FromDays(30)),

            AddressScreening.Unavailable(
                Chain.Ethereum, "0x1111111111111111111111111111111111111111",
                ScreeningPurpose.PayoutDestination, "MistTrack", "MistTrack could not be reached.",
                policyDescription: "block>=91;review>=71;always_block_direct_only=sanctioned_entity",
                screenedAt: T0.AddHours(4)));

        await context.SaveChangesAsync(Ct);
    }

    // A day after the seed, so the completed rows (fresh for 30 days) are still fresh and the failed one is not.
    private static AddressScreeningDirectory NewDirectory(ComplianceDbContext context, DateTimeOffset? now = null) =>
        new(context, new TestClock(now ?? T0.AddDays(1)));

    [Fact]
    public async Task Rows_come_back_newest_first()
    {
        await using var context = NewContext();

        var (items, total) = await NewDirectory(context).SearchAsync(new ScreeningAdminFilter(), 1, 50, Ct);

        total.ShouldBe(5);
        items.Select(i => i.ScreenedAt).ShouldBe(items.Select(i => i.ScreenedAt).OrderByDescending(t => t));
    }

    [Fact]
    public async Task A_filter_narrows_the_total_and_not_just_the_page()
    {
        await using var context = NewContext();

        var (items, total) = await NewDirectory(context)
            .SearchAsync(new ScreeningAdminFilter(Decision: ScreeningDecision.Block), 1, 50, Ct);

        // Both Block rows, including the one whose address has since been re-screened clean — the list is
        // the history, not the current standing.
        total.ShouldBe(2);
        items.ShouldAllBe(i => i.Decision == ScreeningDecision.Block);
    }

    [Fact]
    public async Task Filters_and_together()
    {
        await using var context = NewContext();

        var (_, total) = await NewDirectory(context).SearchAsync(
            new ScreeningAdminFilter(Chain: Chain.Tron, Purpose: ScreeningPurpose.SettlementWallet), 1, 50, Ct);

        total.ShouldBe(1);
    }

    [Fact]
    public async Task An_address_filter_returns_that_address_whole_history()
    {
        await using var context = NewContext();

        var (items, total) = await NewDirectory(context)
            .SearchAsync(new ScreeningAdminFilter(Address: Rehabilitated), 1, 50, Ct);

        total.ShouldBe(2);
        items.First().Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>
    /// The counters answer "how much is outstanding", so an address must count once, at its latest verdict.
    /// Counting rows instead would report the re-screened address as both Blocked and Allowed, and would
    /// grow every time a clean address is routinely re-checked — a number that inflates with age looks like
    /// a workload and is worse than no number at all.
    /// </summary>
    [Fact]
    public async Task Counters_count_each_address_once_at_its_latest_verdict()
    {
        await using var context = NewContext();

        var counts = await NewDirectory(context).GetDecisionCountsAsync(Chain.Tron, Ct);

        counts.TryGetValue(ScreeningDecision.Allow, out var allowed);
        counts.TryGetValue(ScreeningDecision.Block, out var blocked);

        // Clean + Rehabilitated (whose Block is superseded), and only the genuinely blocked one.
        allowed.ShouldBe(2);
        blocked.ShouldBe(1);
    }

    [Fact]
    public async Task Counters_are_scoped_to_the_chain_asked_for()
    {
        await using var context = NewContext();

        var tron = await NewDirectory(context).GetDecisionCountsAsync(Chain.Tron, Ct);
        var all = await NewDirectory(context).GetDecisionCountsAsync(null, Ct);

        tron.ContainsKey(ScreeningDecision.Unavailable).ShouldBeFalse();
        all[ScreeningDecision.Unavailable].ShouldBe(1);
    }

    /// <summary>
    /// The list omits the provider payload on purpose — fifty rows would otherwise drag fifty JSON blobs
    /// across the wire to render a table that shows none of them. The detail read is where it appears.
    /// </summary>
    [Fact]
    public async Task The_raw_provider_payload_is_returned_only_by_the_detail_read()
    {
        await using var context = NewContext();
        var directory = NewDirectory(context);

        var (items, _) = await directory.SearchAsync(new ScreeningAdminFilter(Address: Clean), 1, 50, Ct);
        var detail = await directory.FindByIdAsync(items.Single().Id, Ct);

        detail.ShouldNotBeNull();
        detail.RawResponse.ShouldNotBeNullOrWhiteSpace();
        detail.Row.Address.ShouldBe(Clean);
    }

    /// <summary>
    /// A row may carry a sanctions indicator and still read Allow, because indirect exposure is recorded as
    /// evidence while only a direct designation forces a Block. A screen that hid the indicator would leave
    /// a reviewer unable to see what the vendor actually reported.
    /// </summary>
    [Fact]
    public async Task An_allowed_row_still_surfaces_the_indicators_behind_it()
    {
        await using var context = NewContext();

        var (items, _) = await NewDirectory(context)
            .SearchAsync(new ScreeningAdminFilter(Address: Clean), 1, 50, Ct);

        var row = items.Single();
        row.Decision.ShouldBe(ScreeningDecision.Allow);
        row.Reasons.ShouldContain("sanctioned_entity");
        row.PolicyDescription.ShouldContain("always_block_direct_only");
    }

    [Fact]
    public async Task An_unknown_id_is_not_found_rather_than_an_empty_record()
    {
        await using var context = NewContext();

        (await NewDirectory(context).FindByIdAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_unavailable_row_reports_why_and_is_never_reusable()
    {
        await using var context = NewContext();

        var (items, _) = await NewDirectory(context)
            .SearchAsync(new ScreeningAdminFilter(Decision: ScreeningDecision.Unavailable), 1, 50, Ct);

        var row = items.Single();
        row.FailureReason.ShouldNotBeNullOrWhiteSpace();
        row.Score.ShouldBeNull();

        // Null freshness is what stops one outage pinning an address to "unknown" for the whole cache window.
        row.FreshUntil.ShouldBeNull();
    }

    // ---- Current verdicts (REQ-26a) ---------------------------------------------------------------------

    private static AddressScreening Row(
        string address, ScreeningPurpose purpose, ScreeningDecision decision, DateTimeOffset at) =>
        AddressScreening.Completed(
            Chain.Tron, address, purpose, "MistTrack", decision,
            decision == ScreeningDecision.Block ? 95 : 3, "Low", [], addressLabel: null, reportUrl: null,
            rawResponse: null, policyDescription: "test", screenedAt: at, freshFor: TimeSpan.FromDays(30));

    /// <summary>One save per row, so Seq records the order the rows were written in.</summary>
    private static async Task InsertInOrderAsync(params AddressScreening[] rows)
    {
        foreach (var row in rows)
        {
            await using var context = NewContext();
            context.AddressScreenings.Add(row);
            await context.SaveChangesAsync(Ct);
        }
    }

    /// <summary>
    /// The defect this read exists to fix. The history list still returns the rehabilitated address under
    /// Block through its old row; the current list must not, or a work queue never empties.
    /// </summary>
    [Fact]
    public async Task An_address_leaves_the_current_block_list_once_it_has_been_cleared()
    {
        await using var context = NewContext();

        var page = await NewDirectory(context).SearchCurrentAsync(
            new CurrentVerdictFilter(Decision: ScreeningDecision.Block), 1, 50, Ct);

        page.TotalCount.ShouldBe(1);
        page.Items.ShouldHaveSingleItem().Address.ShouldBe(Flagged);
    }

    [Fact]
    public async Task The_current_list_counts_addresses_not_rows()
    {
        await using var context = NewContext();
        var directory = NewDirectory(context);

        var current = await directory.SearchCurrentAsync(new CurrentVerdictFilter(), 1, 50, Ct);
        var (_, historyTotal) = await directory.SearchAsync(new ScreeningAdminFilter(), 1, 50, Ct);

        current.TotalCount.ShouldBe(4, "four addresses");
        historyTotal.ShouldBe(5, "five rows, one address screened twice");
        current.Items.Select(i => i.Address).ShouldBeUnique();
    }

    /// <summary>
    /// Purpose selects addresses, the verdict stays the latest row. A flagged deposit address re-screened from
    /// the payout screen must stay in the deposit queue even though its latest row now says PayoutDestination.
    /// </summary>
    [Fact]
    public async Task Purpose_selects_addresses_but_the_verdict_stays_the_latest_row()
    {
        const string StillBad = "TStillBad0000000000000000000000000";
        const string NowClean = "TNowClean0000000000000000000000000";

        await InsertInOrderAsync(
            Row(StillBad, ScreeningPurpose.DepositAddress, ScreeningDecision.Block, T0.AddHours(5)),
            Row(StillBad, ScreeningPurpose.PayoutDestination, ScreeningDecision.Block, T0.AddHours(6)),
            Row(NowClean, ScreeningPurpose.DepositAddress, ScreeningDecision.Block, T0.AddHours(5)),
            Row(NowClean, ScreeningPurpose.PayoutDestination, ScreeningDecision.Allow, T0.AddHours(6)));

        await using var context = NewContext();

        var blocked = await NewDirectory(context).SearchCurrentAsync(
            new CurrentVerdictFilter(Decision: ScreeningDecision.Block, Purpose: ScreeningPurpose.DepositAddress),
            1, 50, Ct);

        var row = blocked.Items.ShouldHaveSingleItem();
        row.Address.ShouldBe(StillBad);
        row.Purpose.ShouldBe(ScreeningPurpose.PayoutDestination, "the verdict is the latest row, whatever its purpose");

        // The summary describes the deposit-address population, not the whole table.
        blocked.Summary[ScreeningDecision.Block].ShouldBe(1);
        blocked.Summary[ScreeningDecision.Allow].ShouldBe(1);
        blocked.Summary.Values.Sum().ShouldBe(2);
    }

    [Fact]
    public async Task Stale_means_expired_or_a_failed_screening()
    {
        await using var context = NewContext();

        var dueTomorrow = await NewDirectory(context).SearchCurrentAsync(
            new CurrentVerdictFilter(Stale: true), 1, 50, Ct);
        dueTomorrow.Items.ShouldHaveSingleItem().Decision.ShouldBe(ScreeningDecision.Unavailable);

        var freshTomorrow = await NewDirectory(context).SearchCurrentAsync(
            new CurrentVerdictFilter(Stale: false), 1, 50, Ct);
        freshTomorrow.TotalCount.ShouldBe(3);

        var dueNextMonth = await NewDirectory(context, T0.AddDays(40)).SearchCurrentAsync(
            new CurrentVerdictFilter(Stale: true), 1, 50, Ct);
        dueNextMonth.TotalCount.ShouldBe(4, "every verdict has expired");
    }

    [Fact]
    public async Task The_summary_honours_the_population_but_not_the_decision_filter()
    {
        await using var context = NewContext();

        var page = await NewDirectory(context).SearchCurrentAsync(
            new CurrentVerdictFilter(Chain: Chain.Tron, Decision: ScreeningDecision.Allow), 1, 50, Ct);

        page.TotalCount.ShouldBe(2);
        page.Summary[ScreeningDecision.Allow].ShouldBe(2);
        page.Summary[ScreeningDecision.Block].ShouldBe(1);
        page.Summary.ContainsKey(ScreeningDecision.Unavailable).ShouldBeFalse("the Ethereum row is outside the chain filter");
    }

    /// <summary>
    /// Two rows with the same timestamp. Every reader must pick the same one — the row written last — or the
    /// counters, the list, the batch lookup and the payout gate's cache probe could disagree about one address.
    /// </summary>
    [Fact]
    public async Task A_timestamp_tie_resolves_to_the_row_written_last_in_every_reader()
    {
        const string Tie = "TTimestampTie000000000000000000000";
        var at = T0.AddHours(10);

        await InsertInOrderAsync(
            Row(Tie, ScreeningPurpose.PayoutDestination, ScreeningDecision.Block, at),
            Row(Tie, ScreeningPurpose.PayoutDestination, ScreeningDecision.Allow, at));

        await using var context = NewContext();
        var directory = NewDirectory(context);

        var current = await directory.SearchCurrentAsync(new CurrentVerdictFilter(), 1, 50, Ct);
        current.Items.Single(i => i.Address == Tie).Decision.ShouldBe(ScreeningDecision.Allow);

        var counts = await directory.GetDecisionCountsAsync(Chain.Tron, Ct);
        counts[ScreeningDecision.Allow].ShouldBe(3);
        counts[ScreeningDecision.Block].ShouldBe(1);

        var lookup = await directory.FindLatestForAddressesAsync(Chain.Tron, [Tie], Ct);
        lookup.ShouldHaveSingleItem().Screening!.Decision.ShouldBe(ScreeningDecision.Allow);

        var probe = await new AddressScreeningRepository(context).FindLatestAsync(Chain.Tron, Tie, Ct);
        probe!.Decision.ShouldBe(ScreeningDecision.Allow, "the cache probe the payout gate uses");
    }

    // ---- Batch lookup (REQ-26b) -------------------------------------------------------------------------

    [Fact]
    public async Task The_batch_lookup_answers_every_address_in_request_order()
    {
        const string Unscreened = "TNeverScreened00000000000000000000";
        var otherSpelling = Clean.ToLowerInvariant();

        await using var context = NewContext();

        var result = await NewDirectory(context).FindLatestForAddressesAsync(
            Chain.Tron, [Flagged, Unscreened, otherSpelling, Rehabilitated], Ct);

        result.Select(r => r.Address).ShouldBe([Flagged, Unscreened, otherSpelling, Rehabilitated]);
        result[0].Screening!.Decision.ShouldBe(ScreeningDecision.Block);
        result[1].Screening.ShouldBeNull("null means never screened");

        // Echoed as sent, matched through the case-insensitive collation.
        result[2].Screening!.Decision.ShouldBe(ScreeningDecision.Allow);

        // Its latest verdict, not its old Block row.
        result[3].Screening!.Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>Exact duplicates collapse. Case variants do not: TRON addresses are Base58, where case is
    /// significant, and the caller keys its lookup on the exact string it sent.</summary>
    [Fact]
    public async Task The_batch_lookup_collapses_exact_duplicates_only()
    {
        await using var context = NewContext();

        var result = await NewDirectory(context).FindLatestForAddressesAsync(
            Chain.Tron, [Clean, Clean, Clean.ToLowerInvariant()], Ct);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_empty_batch_lookup_returns_nothing_and_blank_entries_come_back_unscreened()
    {
        await using var context = NewContext();
        var directory = NewDirectory(context);

        (await directory.FindLatestForAddressesAsync(Chain.Tron, [], Ct)).ShouldBeEmpty();

        var blanks = await directory.FindLatestForAddressesAsync(Chain.Tron, ["", "  "], Ct);
        blanks.Count.ShouldBe(2);
        blanks.ShouldAllBe(b => b.Screening == null);
    }

    [Fact]
    public async Task The_batch_lookup_is_scoped_to_its_chain()
    {
        await using var context = NewContext();

        var result = await NewDirectory(context).FindLatestForAddressesAsync(Chain.Ethereum, [Clean], Ct);

        result.ShouldHaveSingleItem().Screening.ShouldBeNull();
    }
}
