using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;

/// <summary>
/// One screening as the back-office shows it. A projection of the evidence row, deliberately without the
/// provider's raw payload: a list of fifty rows would otherwise drag fifty JSON blobs across the wire to
/// render a table that shows none of them. <see cref="IAddressScreeningDirectory.FindByIdAsync"/> returns
/// the payload when someone actually opens a record.
/// </summary>
/// <param name="Decision">OUR decision under the policy in force at the time, not the vendor's band.</param>
/// <param name="Score">Provider score 0-100. Null when the screening produced no verdict.</param>
/// <param name="Reasons">Risk indicators, INCLUDING indirect exposure. Present as evidence for a human;
/// only a direct designation can force a Block, so a row may legitimately list a sanctions indicator and
/// still read Allow.</param>
/// <param name="FailureReason">Why no verdict was obtained. Set only for an Unavailable row.</param>
/// <param name="PolicyDescription">Thresholds in force when this was decided, so an old row explains
/// itself without reference to today's configuration.</param>
/// <param name="FreshUntil">When the result stops being reusable. Null for a failed screening, which is
/// never reused.</param>
public sealed record ScreeningAdminRow(
    Guid Id,
    Chain Chain,
    string Address,
    ScreeningPurpose Purpose,
    string Provider,
    ScreeningDecision Decision,
    int? Score,
    string? RiskLevel,
    IReadOnlyList<string> Reasons,
    string? AddressLabel,
    string? ReportUrl,
    string? FailureReason,
    string PolicyDescription,
    DateTimeOffset ScreenedAt,
    DateTimeOffset? FreshUntil);

/// <summary>One screening plus the provider's verbatim payload, for a staff member investigating a single
/// decision. The payload is what settles a dispute — it shows what the vendor actually returned rather
/// than our parse of it.</summary>
public sealed record ScreeningAdminDetail(ScreeningAdminRow Row, string? RawResponse);

/// <summary>
/// Filter for the staff screening list. Every field is optional and they AND together.
/// </summary>
/// <param name="Address">Exact match, case-insensitive. Deliberately not a substring search: a partial
/// address match invites reading one address's history as another's, and staff arrive here holding a full
/// address from a payout or a settlement wallet, never a fragment.</param>
public sealed record ScreeningAdminFilter(
    Chain? Chain = null,
    ScreeningDecision? Decision = null,
    ScreeningPurpose? Purpose = null,
    string? Address = null,
    DateTimeOffset? FromDate = null,
    DateTimeOffset? ToDate = null);

/// <summary>
/// The staff read seam over the evidence trail (§4.5 — Contracts only, so a host never reaches into this
/// module's persistence). Read-only by construction: the trail is append-only, so there is nothing here
/// to edit. Re-screening an address appends a new row through
/// <see cref="IAddressScreeningService.ReScreenAsync"/> rather than changing an old one.
/// </summary>
public interface IAddressScreeningDirectory
{
    /// <summary>Newest first, because the question staff arrive with is almost always "what just got
    /// flagged", and an address's current verdict is its most recent row.</summary>
    Task<(IReadOnlyList<ScreeningAdminRow> Items, int TotalCount)> SearchAsync(
        ScreeningAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>One record with the provider's raw payload. Null when the id is unknown.</summary>
    Task<ScreeningAdminDetail?> FindByIdAsync(Guid screeningId, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many rows fall under each decision, for the queue counters. Counts DISTINCT ADDRESSES at their
    /// most recent verdict, not rows: an address screened weekly for a year would otherwise dominate the
    /// "blocked" count fifty times over and make the number useless as a measure of outstanding work.
    /// </summary>
    Task<IReadOnlyDictionary<ScreeningDecision, int>> GetDecisionCountsAsync(
        Chain? chain, CancellationToken cancellationToken = default);

    /// <summary>
    /// One row per address, carrying that address's CURRENT verdict — the read a work queue needs.
    ///
    /// <para>Distinct from <see cref="SearchAsync"/>, which filters history rows. There, an address blocked
    /// and later cleared still matches <c>decision=Block</c> through its old row, so a queue built on it never
    /// empties. Here every filter applies to the latest row only.</para>
    /// </summary>
    Task<CurrentVerdictPage> SearchCurrentAsync(
        CurrentVerdictFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current verdict for each of the given addresses on one chain, in one round trip. Reads stored
    /// evidence only: no provider call, no quota, and no cache bypass.
    ///
    /// <para>Returns one entry per distinct address requested, in request order, echoing each address exactly
    /// as sent. <c>Screening</c> is null when the address has never been screened — and only then, because an
    /// <c>Unavailable</c> verdict is a screening that happened. A caller that had to infer "never screened"
    /// from a missing entry would also infer it from a typo.</para>
    /// </summary>
    Task<IReadOnlyList<LatestScreeningLookup>> FindLatestForAddressesAsync(
        Chain chain, IReadOnlyList<string> addresses, CancellationToken cancellationToken = default);
}

/// <summary>
/// Filter for the current-verdict list. Every field is optional and they AND together.
/// </summary>
/// <param name="Decision">Matched against each address's latest row.</param>
/// <param name="Purpose">Selects addresses that have EVER been screened for this purpose. The verdict shown
/// is still the latest row, whatever its purpose.</param>
/// <param name="Stale">True: the latest verdict has expired or was a failed screening, so the address is due
/// for another look. False: the latest verdict is still fresh.</param>
public sealed record CurrentVerdictFilter(
    Chain? Chain = null,
    ScreeningDecision? Decision = null,
    ScreeningPurpose? Purpose = null,
    bool? Stale = null);

/// <param name="TotalCount">Addresses matching every filter — not rows.</param>
/// <param name="Summary">Current verdicts by decision across the same population as the list: every filter
/// applied except <c>decision</c>, so the counters beside a narrowed list describe the addresses it is drawn
/// from rather than the whole table.</param>
public sealed record CurrentVerdictPage(
    IReadOnlyList<ScreeningAdminRow> Items,
    int TotalCount,
    IReadOnlyDictionary<ScreeningDecision, int> Summary);

/// <summary>One answer from the batch lookup. <paramref name="Screening"/> is null only when the address has
/// never been screened.</summary>
public sealed record LatestScreeningLookup(string Address, ScreeningAdminRow? Screening);

public static class ScreeningLookupLimits
{
    /// <summary>Most addresses one batch lookup accepts — the list's own page-size ceiling, so one page of any
    /// address list fits in one request. Above it the request is refused, never truncated: silently answering
    /// the first 200 would render every address after them as never screened.</summary>
    public const int MaxAddresses = 200;
}
