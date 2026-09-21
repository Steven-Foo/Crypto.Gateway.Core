using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;

/// <summary>
/// One version of the screening thresholds, as set by staff.
///
/// <para><b>Append-only, like the evidence it governs.</b> Changing the policy inserts a new row; nothing is
/// ever updated in place. That is not tidiness — a compliance decision has to be explainable months later,
/// and the question "what were the thresholds on the day we allowed this payout" cannot be answered from a
/// mutable settings row. The evidence row stamps the policy text at decision time, and this table says who
/// chose it and when.</para>
///
/// <para><b>Only the tuning knobs live here.</b> The master switches — whether screening runs at all, whether
/// payouts are gated, whether settlement wallets are checked — stay in configuration, deliberately. Those
/// decide whether a control exists; these decide how it is calibrated. A stolen admin session should not be
/// able to silently switch off the gate that holds money, and keeping that in config means an attacker needs
/// infrastructure access rather than a browser tab.</para>
///
/// <para><b>The always-block designation list is add-only.</b> Staff can add to it; they cannot remove what
/// the platform ships with. A sanctions override is precisely the rule that should not be deletable through
/// a web form.</para>
///
/// <para>No ledger impact and no keys (§10).</para>
/// </summary>
public sealed class ScreeningPolicyVersion : Entity<Guid>
{
    private ScreeningPolicyVersion(
        Guid id, int blockScore, int reviewScore, int cacheDays, int indirectReviewMaxHops,
        decimal indirectReviewMinPercent, string extraAlwaysBlockIndicatorsCsv, string? note,
        string updatedBy, DateTimeOffset updatedAt) : base(id)
    {
        BlockScore = blockScore;
        ReviewScore = reviewScore;
        CacheDays = cacheDays;
        IndirectReviewMaxHops = indirectReviewMaxHops;
        IndirectReviewMinPercent = indirectReviewMinPercent;
        ExtraAlwaysBlockIndicatorsCsv = extraAlwaysBlockIndicatorsCsv;
        Note = note;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    private ScreeningPolicyVersion() : base(Guid.Empty)
    {
    }

    /// <summary>Score at or above which an address is refused outright.</summary>
    public int BlockScore { get; private set; }

    /// <summary>Score at or above which an address is held for a human.</summary>
    public int ReviewScore { get; private set; }

    /// <summary>How long a completed screening may be reused. The main lever on provider quota.</summary>
    public int CacheDays { get; private set; }

    /// <summary>Hop limit for the proximity rule. Zero disables it.</summary>
    public int IndirectReviewMaxHops { get; private set; }

    /// <summary>Volume share an indirect exposure must reach to count, 0-100.</summary>
    public decimal IndirectReviewMinPercent { get; private set; }

    /// <summary>Designations staff have ADDED, joined with '|'. The platform's own list is always in force
    /// on top of these and cannot be removed here.</summary>
    public string ExtraAlwaysBlockIndicatorsCsv { get; private set; } = string.Empty;

    /// <summary>Why the change was made. Optional, but the difference between a readable history and a
    /// list of numbers nobody can account for.</summary>
    public string? Note { get; private set; }

    /// <summary>Who set it. A threshold change is a compliance act and must be attributable.</summary>
    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<string> ExtraAlwaysBlockIndicators() =>
        string.IsNullOrEmpty(ExtraAlwaysBlockIndicatorsCsv)
            ? []
            : ExtraAlwaysBlockIndicatorsCsv.Split('|', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Validates and creates a new version. Validation lives here rather than at the edge because these are
    /// the rules themselves, not input formatting — a host, a seeder and a test must all be held to them.
    /// </summary>
    public static Result<ScreeningPolicyVersion> Create(
        int blockScore,
        int reviewScore,
        int cacheDays,
        int indirectReviewMaxHops,
        decimal indirectReviewMinPercent,
        IEnumerable<string>? extraAlwaysBlockIndicators,
        string? note,
        string updatedBy,
        DateTimeOffset now)
    {
        if (blockScore is < 1 or > 100)
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.InvalidScore);
        }

        if (reviewScore is < 1 or > 100)
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.InvalidScore);
        }

        // A review floor above the block floor would mean nothing ever reaches review: everything that
        // qualified would already have been refused. Silently useless is worse than refused.
        if (reviewScore > blockScore)
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.ReviewAboveBlock);
        }

        // Zero would re-screen every address on every sighting and exhaust the daily quota; an unbounded
        // window would let a verdict stand long after it stopped meaning anything.
        if (cacheDays is < 1 or > 365)
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.InvalidCacheDays);
        }

        // Zero disables the proximity rule. Beyond about ten hops the finding describes the shape of the
        // network rather than the address, so the ceiling is a guard against a setting that only looks
        // cautious.
        if (indirectReviewMaxHops is < 0 or > 10)
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.InvalidHops);
        }

        if (indirectReviewMinPercent is < 0 or > 100)
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.InvalidPercent);
        }

        if (string.IsNullOrWhiteSpace(updatedBy))
        {
            return Result.Failure<ScreeningPolicyVersion>(ComplianceErrors.UnattributedChange);
        }

        var extras = (extraAlwaysBlockIndicators ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Result.Success(new ScreeningPolicyVersion(
            Guid.CreateVersion7(), blockScore, reviewScore, cacheDays, indirectReviewMaxHops,
            indirectReviewMinPercent, string.Join('|', extras), note?.Trim(), updatedBy.Trim(), now));
    }
}
