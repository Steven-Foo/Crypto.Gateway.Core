using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;

/// <summary>
/// What the platform does about an address, once its risk is known. Deliberately three outcomes plus an
/// explicit "we do not know", because collapsing the unknown into either <see cref="Allow"/> or
/// <see cref="Block"/> is the decision that gets compliance integrations wrong: fail-open silently drops
/// the control the moment the vendor has an outage, and fail-closed halts every payout on someone else's
/// availability. <see cref="Unavailable"/> keeps that a visible, separately-handled third case.
/// </summary>
public enum ScreeningDecision
{
    /// <summary>Risk is below every configured threshold. Proceed.</summary>
    Allow = 0,

    /// <summary>Risk warrants a human look before money moves. Never an automatic refusal.</summary>
    Review = 1,

    /// <summary>Risk is severe enough to refuse outright under the policy in force.</summary>
    Block = 2,

    /// <summary>No verdict could be obtained (provider down, quota exhausted, timeout). NOT a clean result —
    /// the caller decides what to do, and the recommended handling is to hold for review rather than to
    /// assume either safety or guilt.</summary>
    Unavailable = 3
}

/// <summary>
/// Why an address is being screened. Recorded on the evidence so a later audit can tell a payout
/// destination check apart from a settlement-wallet whitelisting, and so per-purpose policy can diverge
/// later without re-screening history.
/// </summary>
public enum ScreeningPurpose
{
    /// <summary>A user payout destination supplied on the request.</summary>
    PayoutDestination = 0,

    /// <summary>A merchant settlement wallet being whitelisted by staff.</summary>
    SettlementWallet = 1,

    /// <summary>The sender of an inbound deposit. Recorded only; an arrived deposit is never refused.</summary>
    DepositSource = 2,

    /// <summary>
    /// One of OUR OWN receiving addresses, screened for contamination.
    ///
    /// <para>Deliberately distinct from <see cref="DepositSource"/>, which is a counterparty. This is the
    /// only inbound control that is actually available: a transfer cannot be screened while it is in flight,
    /// and once it lands it cannot be refused — a deposit that arrived is a fact, and a frozen merchant's
    /// deposits still credit the ledger (§14). What CAN be done is watch the addresses we issue, because a
    /// provider scores an address from its transaction graph, so tainted inflow shows up on our own address
    /// after the fact.</para>
    ///
    /// <para>A flag here is therefore information, never a refusal. It tells staff which address received
    /// something worth investigating.</para>
    /// </summary>
    DepositAddress = 3,

    /// <summary>
    /// A cold collection wallet — one of the platform's own sweep destinations, screened when staff register
    /// it and re-screened on demand.
    ///
    /// <para>Recorded but never acted on automatically. The quarantine destination is <em>expected</em> to
    /// score badly, because tainted sweeps are deliberately sent to it; treating a bad verdict as a refusal
    /// would disable the segregation exactly when it is working.</para>
    /// </summary>
    ColdCollectionWallet = 4
}

/// <summary>
/// The answer a caller acts on, plus enough evidence to explain it to a human without a second lookup.
/// <see cref="Score"/> and <see cref="RiskLevel"/> are the provider's raw output; <see cref="Decision"/>
/// is OUR policy applied to it. Both are kept because they answer different questions: the score is what
/// the vendor said, the decision is what we did about it, and a policy change must never rewrite history.
/// </summary>
/// <param name="ScreeningId">The stored evidence record, for audit and for an ops drill-down.</param>
/// <param name="Decision">What the caller should do.</param>
/// <param name="Score">Provider risk score, 0-100. Null when <see cref="ScreeningDecision.Unavailable"/>.</param>
/// <param name="RiskLevel">Provider band (Low/Moderate/High/Severe). Null when unavailable.</param>
/// <param name="Reasons">Human-readable risk indicators, e.g. "Sanctioned Entity", "Mixer".</param>
/// <param name="AddressLabel">Entity label the provider attributes to the address, when it knows one.</param>
/// <param name="ReportUrl">Provider's full report, for a human investigating a Review or Block.</param>
/// <param name="ScreenedAt">When the evidence was obtained — a score is a snapshot, not a standing fact.</param>
/// <param name="FromCache">True when served from a prior, still-fresh screening rather than a new call.</param>
public sealed record ScreeningVerdict(
    Guid ScreeningId,
    ScreeningDecision Decision,
    int? Score,
    string? RiskLevel,
    IReadOnlyList<string> Reasons,
    string? AddressLabel,
    string? ReportUrl,
    DateTimeOffset ScreenedAt,
    bool FromCache);

/// <summary>
/// The one seam other modules use (§4.5) — Withdrawal for payout destinations, Merchant for settlement
/// wallets, later Deposit for senders. Callers get a verdict and never learn which vendor produced it,
/// which is what keeps the vendor swappable.
/// </summary>
public interface IAddressScreeningService
{
    /// <summary>
    /// Screen one address and record the evidence. Returns a verdict rather than throwing on an
    /// unavailable provider — an outage is an expected business condition here, not an exception (§7.1),
    /// and surfaces as <see cref="ScreeningDecision.Unavailable"/>.
    /// </summary>
    Task<ScreeningVerdict> ScreenAsync(
        Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent stored verdict for an address, without contacting the provider. For read screens
    /// and for a caller that must not spend quota. Null when the address has never been screened.
    /// </summary>
    Task<ScreeningVerdict?> FindLatestAsync(
        Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Of the addresses given, which have no still-fresh verdict — i.e. which would actually cost a provider
    /// call. Contacts nobody; this is a single indexed read.
    ///
    /// <para><b>Why a bulk query rather than screening the list and letting the cache absorb it.</b> A caller
    /// sweeping its own addresses may hold thousands. Calling <see cref="ScreenAsync"/> on each would be
    /// correct and would spend no quota on the fresh ones, but it would still be one database round trip per
    /// address per pass. This answers the same question in one query, and <paramref name="limit"/> lets the
    /// caller bound what a single pass will spend before it starts.</para>
    /// </summary>
    /// <param name="limit">Most addresses to return. The caller's quota budget for one pass.</param>
    Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
        Chain chain,
        IReadOnlyCollection<string> addresses,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Screen an address again, ignoring any cached result, and append a new evidence row.
    ///
    /// <para><b>Why this exists separately from <see cref="ScreenAsync"/>.</b> The cache is what keeps a
    /// burst of payouts inside the provider's rate limit, so the money paths must never bypass it. But a
    /// cached verdict is precisely what an operator needs to override: an address may have been screened
    /// while the provider was degraded, or its status may have changed inside the cache window. Making the
    /// bypass a distinct, separately-permissioned call keeps it a deliberate human act that spends quota
    /// knowingly, rather than a flag an automated path could set.</para>
    ///
    /// <para>It appends rather than replaces, like every other screening — the old verdict remains the
    /// proof of what a payout was judged on at the time (§14's append-only discipline).</para>
    /// </summary>
    Task<ScreeningVerdict> ReScreenAsync(
        Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default);
}
