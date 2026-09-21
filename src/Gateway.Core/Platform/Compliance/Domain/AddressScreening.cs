using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;

/// <summary>
/// One screening of one address: what the provider said, and what we decided about it.
///
/// <para>This is an <b>evidence record, not a state machine</b>. It is written once and never edited —
/// re-screening the same address appends a new row rather than updating this one. That is the same
/// append-only discipline the ledger uses (§14) and for the same reason: a compliance decision has to be
/// explainable months later, and an overwritten score destroys the only proof of why a payout was
/// allowed or refused on the day.</para>
///
/// <para><b>Both the raw score and our decision are stored.</b> The score is the vendor's claim; the
/// decision is our policy applied to it. Keeping only the score would mean a later threshold change
/// silently rewrites what we appear to have decided in the past, and keeping only the decision would
/// leave us unable to show what it was based on. <see cref="PolicyDescription"/> pins the thresholds
/// that were actually in force, so the row explains itself without reference to today's config.</para>
///
/// <para>No money and no keys pass through here (§10) — the module reads a public address and stores a
/// third party's opinion of it, so there is no ledger impact of any kind.</para>
/// </summary>
public sealed class AddressScreening : Entity<Guid>
{
    private AddressScreening(
        Guid id, Chain chain, string address, ScreeningPurpose purpose, string provider,
        ScreeningDecision decision, int? score, string? riskLevel, string reasonsCsv,
        string? addressLabel, string? reportUrl, string? rawResponse, string? failureReason,
        string policyDescription, DateTimeOffset screenedAt, DateTimeOffset? freshUntil) : base(id)
    {
        Chain = chain;
        Address = address;
        Purpose = purpose;
        Provider = provider;
        Decision = decision;
        Score = score;
        RiskLevel = riskLevel;
        ReasonsCsv = reasonsCsv;
        AddressLabel = addressLabel;
        ReportUrl = reportUrl;
        RawResponse = rawResponse;
        FailureReason = failureReason;
        PolicyDescription = policyDescription;
        ScreenedAt = screenedAt;
        FreshUntil = freshUntil;
    }

    private AddressScreening() : base(Guid.Empty)
    {
    }

    public Chain Chain { get; private set; }

    /// <summary>The screened address, exactly as the caller supplied it. Case is preserved for the audit
    /// trail; lookups normalise separately, so a case difference never silently splits an address's history.</summary>
    public string Address { get; private set; } = null!;

    public ScreeningPurpose Purpose { get; private set; }

    /// <summary>Which vendor produced this, e.g. "MistTrack". Stored per row because the vendor can change
    /// and a mixed-provider history must stay readable.</summary>
    public string Provider { get; private set; } = null!;

    public ScreeningDecision Decision { get; private set; }

    /// <summary>Provider score 0-100, or null when the screening did not complete.</summary>
    public int? Score { get; private set; }

    public string? RiskLevel { get; private set; }

    /// <summary>Risk indicators joined with '|'. A denormalised string rather than a child table: this is
    /// display and audit text that is never queried by element, so a table would add a join for no gain.</summary>
    public string ReasonsCsv { get; private set; } = string.Empty;

    public string? AddressLabel { get; private set; }

    public string? ReportUrl { get; private set; }

    /// <summary>The provider's verbatim JSON. Kept so a dispute can be settled against what was actually
    /// returned rather than against our parse of it, and so a mapping bug stays diagnosable after the fact.</summary>
    public string? RawResponse { get; private set; }

    /// <summary>Why a screening produced no verdict. Set only for <see cref="ScreeningDecision.Unavailable"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The thresholds in force when this decision was taken, rendered for a human.</summary>
    public string PolicyDescription { get; private set; } = null!;

    public DateTimeOffset ScreenedAt { get; private set; }

    /// <summary>
    /// When this result stops being reusable. A risk score is a snapshot — an address clean today can be
    /// sanctioned next month — so a cached verdict has to expire. Null for a failed screening, which is
    /// never reusable at all.
    /// </summary>
    public DateTimeOffset? FreshUntil { get; private set; }

    /// <summary>A completed screening, with the provider's result and our decision.</summary>
    public static AddressScreening Completed(
        Chain chain, string address, ScreeningPurpose purpose, string provider, ScreeningDecision decision,
        int score, string riskLevel, IEnumerable<string> reasons, string? addressLabel, string? reportUrl,
        string? rawResponse, string policyDescription, DateTimeOffset screenedAt, TimeSpan freshFor) =>
        new(Guid.CreateVersion7(), chain, address, purpose, provider, decision, score, riskLevel,
            string.Join('|', reasons), addressLabel, reportUrl, rawResponse, failureReason: null,
            policyDescription, screenedAt, screenedAt.Add(freshFor));

    /// <summary>
    /// A screening that produced no verdict. Recorded rather than discarded: "we tried and could not tell"
    /// is itself an auditable fact, and silently dropping it would leave a payout held for review with no
    /// visible reason. <see cref="FreshUntil"/> stays null so this is never reused as a cached answer.
    /// </summary>
    public static AddressScreening Unavailable(
        Chain chain, string address, ScreeningPurpose purpose, string provider, string failureReason,
        string policyDescription, DateTimeOffset screenedAt) =>
        new(Guid.CreateVersion7(), chain, address, purpose, provider, ScreeningDecision.Unavailable,
            score: null, riskLevel: null, reasonsCsv: string.Empty, addressLabel: null, reportUrl: null,
            rawResponse: null, failureReason, policyDescription, screenedAt, freshUntil: null);

    public IReadOnlyList<string> Reasons() =>
        string.IsNullOrEmpty(ReasonsCsv) ? [] : ReasonsCsv.Split('|', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when this result may still be served instead of spending a provider call.</summary>
    public bool IsFreshAt(DateTimeOffset now) => FreshUntil is { } until && now < until;
}
