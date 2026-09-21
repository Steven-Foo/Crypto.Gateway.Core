using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

/// <summary>
/// The staff reads over the evidence trail. No tracking anywhere — this projects an append-only table and
/// has nothing to write.
///
/// <para><b>Two different questions, kept apart.</b> <see cref="SearchAsync"/> answers "what has been
/// recorded" and returns every row. <see cref="SearchCurrentAsync"/> and
/// <see cref="FindLatestForAddressesAsync"/> answer "what is true now" and return one row per address. A
/// work queue built on the first would keep showing an address as blocked long after it was cleared, because
/// its old row still says Block.</para>
///
/// <para><b>One definition of "latest", used by every reader.</b> Newest <c>ScreenedAt</c>, then highest
/// <c>Seq</c>. <c>Seq</c> rather than <c>Id</c> because SQL Server orders a <c>uniqueidentifier</c> by its
/// last six bytes first, so ordering by <c>Id</c> is deterministic but unrelated to the order rows were
/// written — even for version-7 GUIDs. <c>Seq</c> is the clustered identity, which is insertion order and
/// therefore what "latest" means. The cache probe in <see cref="AddressScreeningRepository"/> uses the same
/// rule, so the list, the counters, the batch lookup and the payout gate can never pick different rows for
/// the same address.</para>
/// </summary>
public sealed class AddressScreeningDirectory(ComplianceDbContext db, TimeProvider clock) : IAddressScreeningDirectory
{
    internal const string SeqProperty = "Seq";

    public async Task<(IReadOnlyList<ScreeningAdminRow> Items, int TotalCount)> SearchAsync(
        ScreeningAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = Filtered(filter);

        // Counted before paging, so the caller can page through what the filter actually matched rather
        // than through the page it happened to land on.
        var total = await query.CountAsync(cancellationToken);

        var rows = await Project(NewestFirst(query)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(cancellationToken);

        return ([.. rows.Select(ToAdminRow)], total);
    }

    public async Task<CurrentVerdictPage> SearchCurrentAsync(
        CurrentVerdictFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // Captured once, so the stale filter and everything it feeds are judged against one instant.
        var now = clock.GetUtcNow();

        // Reduce FIRST. Every filter below applies to an address's latest row, never to its history —
        // filtering history first is exactly the defect this read exists to avoid.
        var population = LatestRows();

        if (filter.Chain is { } chain)
        {
            population = population.Where(s => s.Chain == chain);
        }

        if (filter.Purpose is { } purpose)
        {
            // Purpose chooses WHICH addresses are in the population, not which row is the verdict. An
            // address belongs if it has ever been screened for this purpose; its verdict is still its
            // latest row, whatever that row's purpose. Matching purpose on the latest row instead would
            // silently drop a flagged deposit address from the deposit queue the moment someone re-screened
            // it from the payout-wallet screen.
            population = population.Where(s => db.AddressScreenings.Any(any =>
                any.Chain == s.Chain && any.Address == s.Address && any.Purpose == purpose));
        }

        if (filter.Stale is { } stale)
        {
            // A null FreshUntil is a failed screening, which is never reused, so it is due by definition.
            population = stale
                ? population.Where(s => s.FreshUntil == null || s.FreshUntil <= now)
                : population.Where(s => s.FreshUntil != null && s.FreshUntil > now);
        }

        // The counters describe the same addresses the list is drawn from — every filter except decision.
        // Honouring decision would collapse the summary to a single bucket; ignoring purpose or stale would
        // count a different set of addresses than the one on screen.
        var summary = await CountByDecisionAsync(population, cancellationToken);

        var matching = filter.Decision is { } decision
            ? population.Where(s => s.Decision == decision)
            : population;

        // Addresses, not rows: after the reduction there is exactly one row per address.
        var total = await matching.CountAsync(cancellationToken);

        var rows = await Project(NewestFirst(matching)
                .Skip((page - 1) * pageSize)
                .Take(pageSize))
            .ToListAsync(cancellationToken);

        return new CurrentVerdictPage([.. rows.Select(ToAdminRow)], total, summary);
    }

    public async Task<IReadOnlyList<LatestScreeningLookup>> FindLatestForAddressesAsync(
        Chain chain, IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
    {
        // Exact duplicates collapse to one entry. Case variants do NOT: TRON addresses are Base58, where case
        // is significant, and the caller keys its lookup on the exact string it sent — so each spelling gets
        // its own entry back, carrying whatever the column's case-insensitive collation matched.
        var requested = addresses
            .Select(a => a ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (requested.Count == 0)
        {
            return [];
        }

        var keys = requested
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byAddress = new Dictionary<string, ScreeningAdminRow>(StringComparer.OrdinalIgnoreCase);

        if (keys.Count > 0)
        {
            var rows = await Project(LatestRows()
                    .Where(s => s.Chain == chain && keys.Contains(s.Address)))
                .ToListAsync(cancellationToken);

            foreach (var row in rows.Select(ToAdminRow))
            {
                byAddress.TryAdd(row.Address, row);
            }
        }

        // One entry per address requested, in request order, echoing the caller's own string. Null means
        // never screened and nothing else — an Unavailable verdict is a screening that happened.
        return
        [
            .. requested.Select(a => new LatestScreeningLookup(
                a, byAddress.TryGetValue(a.Trim(), out var row) ? row : null)),
        ];
    }

    public async Task<ScreeningAdminDetail?> FindByIdAsync(
        Guid screeningId, CancellationToken cancellationToken = default)
    {
        var entity = await db.AddressScreenings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == screeningId, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        return new ScreeningAdminDetail(
            new ScreeningAdminRow(
                entity.Id, entity.Chain, entity.Address, entity.Purpose, entity.Provider, entity.Decision,
                entity.Score, entity.RiskLevel, entity.Reasons(), entity.AddressLabel, entity.ReportUrl,
                entity.FailureReason, entity.PolicyDescription, entity.ScreenedAt, entity.FreshUntil),
            entity.RawResponse);
    }

    public Task<IReadOnlyDictionary<ScreeningDecision, int>> GetDecisionCountsAsync(
        Chain? chain, CancellationToken cancellationToken = default)
    {
        // Counts each ADDRESS once, at its latest verdict — not every row ever written. A counter that
        // inflates with age is worse than no counter, because it looks like a workload.
        var latest = LatestRows();
        if (chain is { } c)
        {
            latest = latest.Where(s => s.Chain == c);
        }

        return CountByDecisionAsync(latest, cancellationToken);
    }

    /// <summary>
    /// Each address's latest row: one for which no newer row exists for the same chain and address.
    ///
    /// <para>Written as NOT EXISTS rather than a group-and-take-first because it stays a plain row set, so
    /// filtering, counting and paging compose on top of it in SQL. It seeks on
    /// <c>IX_AddressScreening_Chain_Address_ScreenedAt</c>. Address equality relies on the column's
    /// case-insensitive collation, the same comparison the cache probe uses.</para>
    /// </summary>
    private IQueryable<AddressScreening> LatestRows() =>
        db.AddressScreenings
            .AsNoTracking()
            .Where(s => !db.AddressScreenings.Any(newer =>
                newer.Chain == s.Chain
                && newer.Address == s.Address
                && (newer.ScreenedAt > s.ScreenedAt
                    || (newer.ScreenedAt == s.ScreenedAt
                        && EF.Property<long>(newer, SeqProperty) > EF.Property<long>(s, SeqProperty)))));

    private static IOrderedQueryable<AddressScreening> NewestFirst(IQueryable<AddressScreening> query) =>
        query
            .OrderByDescending(s => s.ScreenedAt)
            .ThenByDescending(s => EF.Property<long>(s, SeqProperty));

    private static async Task<IReadOnlyDictionary<ScreeningDecision, int>> CountByDecisionAsync(
        IQueryable<AddressScreening> latest, CancellationToken cancellationToken)
    {
        var counts = await latest
            .GroupBy(s => s.Decision)
            .Select(g => new { Decision = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.Decision, x => x.Count);
    }

    /// <summary>Projected in SQL, deliberately omitting RawResponse: the payload is unbounded, so selecting
    /// whole entities would pull a page of JSON blobs to render a table that never shows them.</summary>
    private static IQueryable<RowData> Project(IQueryable<AddressScreening> query) =>
        query.Select(s => new RowData(
            s.Id, s.Chain, s.Address, s.Purpose, s.Provider, s.Decision, s.Score, s.RiskLevel,
            s.ReasonsCsv, s.AddressLabel, s.ReportUrl, s.FailureReason, s.PolicyDescription,
            s.ScreenedAt, s.FreshUntil));

    private static ScreeningAdminRow ToAdminRow(RowData s) => new(
        s.Id, s.Chain, s.Address, s.Purpose, s.Provider, s.Decision, s.Score, s.RiskLevel,
        SplitReasons(s.ReasonsCsv), s.AddressLabel, s.ReportUrl, s.FailureReason,
        s.PolicyDescription, s.ScreenedAt, s.FreshUntil);

    private IQueryable<AddressScreening> Filtered(ScreeningAdminFilter filter)
    {
        var query = db.AddressScreenings.AsNoTracking().AsQueryable();

        if (filter.Chain is { } chain) query = query.Where(s => s.Chain == chain);
        if (filter.Decision is { } decision) query = query.Where(s => s.Decision == decision);
        if (filter.Purpose is { } purpose) query = query.Where(s => s.Purpose == purpose);

        if (!string.IsNullOrWhiteSpace(filter.Address))
        {
            // Equality, relying on the column's case-insensitive collation — the same comparison the cache
            // probe uses, so the list can never show a history the screening path would not have found.
            var address = filter.Address.Trim();
            query = query.Where(s => s.Address == address);
        }

        if (filter.FromDate is { } from) query = query.Where(s => s.ScreenedAt >= from);
        if (filter.ToDate is { } to) query = query.Where(s => s.ScreenedAt <= to);

        return query;
    }

    private static IReadOnlyList<string> SplitReasons(string reasonsCsv) =>
        string.IsNullOrEmpty(reasonsCsv) ? [] : reasonsCsv.Split('|', StringSplitOptions.RemoveEmptyEntries);

    private sealed record RowData(
        Guid Id,
        Chain Chain,
        string Address,
        ScreeningPurpose Purpose,
        string Provider,
        ScreeningDecision Decision,
        int? Score,
        string? RiskLevel,
        string ReasonsCsv,
        string? AddressLabel,
        string? ReportUrl,
        string? FailureReason,
        string PolicyDescription,
        DateTimeOffset ScreenedAt,
        DateTimeOffset? FreshUntil);
}
