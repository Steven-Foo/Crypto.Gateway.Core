namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;

/// <summary>
/// Screening policy, bound from <c>Compliance</c>. Thresholds are expressed as score floors because that
/// is what every vendor in this space actually returns; the provider's own band names are recorded as
/// evidence but never drive the decision, so swapping vendors cannot silently move our risk appetite.
/// </summary>
public sealed class ComplianceOptions
{
    public const string SectionName = "Compliance";

    /// <summary>Master switch. Off ⇒ every call returns <c>Unavailable</c> without contacting anyone, which
    /// is the correct dev/test default: screening must be something you turn ON deliberately, never
    /// something a forgotten config key turns off silently.</summary>
    public bool Enabled { get; set; }

    /// <summary>Score at or above which an address is refused outright. Default 91 = the provider's
    /// "Severe" floor.</summary>
    public int BlockScore { get; set; } = 91;

    /// <summary>Score at or above which an address is held for a human. Default 71 = "High".</summary>
    public int ReviewScore { get; set; } = 71;

    /// <summary>How long a completed screening may be reused before the address is re-screened. Days,
    /// because a risk score changes on the timescale of investigations and sanctions designations, not
    /// minutes. Also the main lever on quota consumption.</summary>
    public int CacheDays { get; set; } = 30;

    /// <summary>
    /// Risk indicators that force a Block regardless of score. A sanctions hit is a legal matter, not a
    /// gradient, so it must not be possible for a threshold tweak to let one through.
    ///
    /// <para><b>Matched only against DIRECT designations</b> — what the provider says about this address
    /// itself — never against exposure inherited through a chain of counterparties. Vendors report both
    /// using the same codes, and indirect exposure to a sanctioned party is near-universal for any address
    /// with exchange history: a live TRX address the vendor scores 3 out of 100 still carries a
    /// <c>sanctioned_entity</c> entry three hops away. Matching those would refuse ordinary destinations.
    /// Indirect exposure is already priced into the score, which <see cref="BlockScore"/> and
    /// <see cref="ReviewScore"/> act on.</para>
    /// </summary>
    public string[] AlwaysBlockIndicators { get; set; } = ["sanctioned_entity", "Sanctioned Entity"];

    /// <summary>
    /// The always-block list, de-duplicated case-insensitively.
    ///
    /// <para><b>Configuration ADDS to the built-in defaults, it does not replace them.</b> That is how
    /// .NET binds a string array onto a property that already holds one, and it is left that way
    /// deliberately: a sanctions override is exactly the rule that should not be removable by editing a
    /// settings file. The consequence is duplicates whenever config repeats a default, which are harmless
    /// to matching but would otherwise be written verbatim into every evidence row — so they are collapsed
    /// here, once, rather than stored as noise in an append-only trail nobody can clean up later.</para>
    /// </summary>
    internal IReadOnlyList<string> EffectiveAlwaysBlockIndicators =>
        [.. AlwaysBlockIndicators
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// How close an INDIRECT exposure must be, in hops, before it is worth a human look. <b>Zero disables
    /// the rule</b>, which is the default.
    ///
    /// <para><b>What it is for.</b> Today all indirect exposure is treated identically: recorded as
    /// evidence, left entirely to the vendor's score. That is the safe default and it is what makes
    /// screening usable at all, but it flattens a real distinction — 60% of volume one hop from a
    /// sanctioned entity and 0.1% five hops away are very different facts, and we currently treat them the
    /// same.</para>
    ///
    /// <para><b>Review, never Block.</b> Block stays reserved for a direct designation, which is a legal
    /// fact about this address. Proximity is a gradient, so the most it can justify is putting a person on
    /// it.</para>
    ///
    /// <para><b>Why it ships disabled.</b> Any values chosen before there is real data would be invented.
    /// Set loosely, every payout queues for staff, which trains people to approve without looking and makes
    /// the control worse than nothing; set tightly, it never fires and nothing has changed. Every screening
    /// stores the full provider payload, so the hop and percentage distribution of real destinations can be
    /// measured once volume has run through, and these numbers chosen from it rather than guessed.</para>
    /// </summary>
    public int IndirectReviewMaxHops { get; set; }

    /// <summary>
    /// How much of the address's volume an indirect exposure must account for before it counts, 0-100.
    /// Paired with <see cref="IndirectReviewMaxHops"/>; both must be satisfied.
    ///
    /// <para>Distance alone is not enough. Nearly every address with exchange history sits a few hops from
    /// something bad at a trivial share of volume, so a hop limit without a weight would flag almost
    /// everything — the failure mode this rule exists to avoid.</para>
    /// </summary>
    public decimal IndirectReviewMinPercent { get; set; }

    /// <summary>
    /// Which risk types the proximity rule applies to. Deliberately the SAME list as
    /// <see cref="AlwaysBlockIndicators"/> rather than a second one to keep in step: the rule reads as
    /// "the findings that would block outright if they described this address get a human look when they
    /// are merely close". Two independent lists would drift, and a drifted compliance rule is worse than a
    /// blunt one.
    /// </summary>
    internal bool ProximityRuleEnabled => IndirectReviewMaxHops > 0;

    /// <summary>Recorded verbatim on every evidence row, so a decision can always be re-read against the
    /// policy actually in force when it was made — including that the override was direct-only, and what
    /// the proximity rule was set to.</summary>
    internal string Describe() =>
        $"block>={BlockScore};review>={ReviewScore};always_block_direct_only={string.Join(',', EffectiveAlwaysBlockIndicators)}"
        + (ProximityRuleEnabled
            ? $";indirect_review<={IndirectReviewMaxHops}hops>={IndirectReviewMinPercent}pct"
            : string.Empty);
}
