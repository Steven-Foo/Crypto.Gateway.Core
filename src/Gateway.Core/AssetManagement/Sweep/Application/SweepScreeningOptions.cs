namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;

/// <summary>What a sweep does when the deposit address has no usable screening verdict.</summary>
public enum UnscreenedSweepAction
{
    /// <summary>
    /// Do not sweep it yet. The default, and the only option that cannot be wrong: the balance simply stays
    /// on the deposit address — which the platform also controls, so nothing is lost or exposed — and the
    /// next pass tries again once a verdict exists.
    /// </summary>
    Hold = 0,

    /// <summary>Treat it as clean and sweep it into the Safe wallet. Keeps concentration running during a
    /// provider outage, at the cost of possibly mixing unscreened funds into clean treasury — which cannot
    /// be undone afterwards.</summary>
    Safe = 1,

    /// <summary>Treat it as suspect and sweep it into the quarantine wallet. Keeps concentration running
    /// without contaminating clean treasury; the cost is clean funds needing a manual move out of
    /// quarantine later.</summary>
    Danger = 2,
}

/// <summary>
/// Whether a deposit address is screened before it is swept, and where the funds go when it is flagged.
/// Bound from <c>Sweep:Screening</c>.
///
/// <para>Separate from <c>Compliance</c> and from the other consumers' switches, as everywhere else in this
/// codebase: Compliance answers "how risky is this address", and each consumer answers "what do I do about
/// it". Here the answer is a routing decision rather than a refusal, which is a different question from the
/// payout gate's and must be switchable on its own.</para>
/// </summary>
public sealed class SweepScreeningOptions
{
    public const string SectionName = "Sweep:Screening";

    /// <summary>
    /// Screen each deposit address before sweeping it and route the funds accordingly. Default <b>false</b>,
    /// which is exactly the behaviour before segregation existed: every sweep goes to the Safe wallet.
    /// Turning it on requires a Danger collection wallet to be registered per chain, or flagged balances
    /// stay put (they are never sent to the Safe wallet as a fallback — that is the one outcome the whole
    /// feature exists to prevent).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// What to do when an address has no fresh verdict — the provider is down, quota is exhausted, or it has
    /// never been screened. Default <see cref="UnscreenedSweepAction.Hold"/>.
    /// </summary>
    public UnscreenedSweepAction OnUnavailable { get; set; } = UnscreenedSweepAction.Hold;

    /// <summary>
    /// How many provider calls one scan pass may spend. The provider allows roughly one call a second and
    /// the payout gate shares the same daily quota, so an unbounded pass over a growing set of deposit
    /// addresses could exhaust the budget that the control actually holding money depends on. Addresses over
    /// the budget are not screened this pass and follow <see cref="OnUnavailable"/>; the next pass picks
    /// them up.
    /// </summary>
    public int MaxScreeningsPerPass { get; set; } = 25;
}
