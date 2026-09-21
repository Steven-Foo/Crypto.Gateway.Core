using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;

/// <summary>
/// Screens an address, applies our policy to the vendor's score, and appends the evidence.
///
/// <para><b>Why the cache is a first-class part of this and not an optimisation.</b> The subscription
/// meters calls per day and per second, so reusing a fresh result is what keeps a burst of payouts inside
/// the rate limit. It also bounds cost when a plan is usage-based. The trade-off is deliberate and
/// bounded by <see cref="ComplianceOptions.CacheDays"/>: a stale-but-fresh-enough verdict is preferred to
/// a rate-limit rejection, because a rejection produces no verdict at all.</para>
///
/// <para><b>Never throws for an unavailable provider.</b> An outage yields
/// <see cref="ScreeningDecision.Unavailable"/> and an evidence row saying so. Callers must decide what an
/// unknown means for their flow; this service refuses to guess on their behalf, because guessing "allow"
/// silently removes the control and guessing "block" hands a third party the power to halt payouts.</para>
/// </summary>
public sealed class AddressScreeningService(
    IAddressRiskProvider provider,
    IAddressScreeningRepository repository,
    IOptions<ComplianceOptions> options,
    ScreeningPolicyProvider policyProvider,
    TimeProvider clock,
    ILogger<AddressScreeningService> logger) : IAddressScreeningService
{
    // Only the MASTER SWITCH is read straight from configuration. Everything else — the thresholds — comes
    // from the policy provider, which lets staff calibrate without a deployment while keeping the decision
    // to run the control at all outside the reach of a web session.
    private readonly ComplianceOptions _options = options.Value;

    public Task<ScreeningVerdict> ScreenAsync(
        Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
        ScreenAsync(chain, address, purpose, bypassCache: false, cancellationToken);

    public Task<ScreeningVerdict> ReScreenAsync(
        Chain chain, string address, ScreeningPurpose purpose, CancellationToken cancellationToken = default) =>
        ScreenAsync(chain, address, purpose, bypassCache: true, cancellationToken);

    private async Task<ScreeningVerdict> ScreenAsync(
        Chain chain, string address, ScreeningPurpose purpose, bool bypassCache,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (!_options.Enabled)
            return await RecordUnavailableAsync(chain, address, purpose, "Screening is disabled by configuration.", now, cancellationToken);

        if (string.IsNullOrWhiteSpace(address))
            return await RecordUnavailableAsync(chain, address ?? string.Empty, purpose, "No address supplied.", now, cancellationToken);

        // A fresh prior result is reused rather than re-billed. Purpose deliberately does NOT narrow this:
        // the risk of an address is a property of the address, not of why we are asking, so a settlement
        // wallet screened yesterday answers a payout question today.
        // A re-screen deliberately skips this lookup entirely. Note the master switch above is NOT
        // skipped: an operator may force a fresh call, but may not turn screening on for one address while
        // it is off for the platform.
        if (!bypassCache)
        {
            var existing = await repository.FindLatestAsync(chain, address, cancellationToken);
            if (existing is not null && existing.IsFreshAt(now))
                return ToVerdict(existing, fromCache: true);
        }

        if (!provider.Supports(chain))
            return await RecordUnavailableAsync(chain, address, purpose, $"{provider.Name} does not cover {chain}.", now, cancellationToken);

        // Resolved once per screening, not per rule, so every decision below is judged against one
        // consistent set of thresholds even if a change lands mid-call.
        var policy = await policyProvider.GetAsync(cancellationToken);

        var report = await provider.GetRiskAsync(chain, address, cancellationToken);
        if (report.IsFailure)
        {
            logger.LogWarning(
                "Address screening unavailable for {Chain} {Address}: {Error}", chain, Mask(address), report.Error!.Code);
            return await RecordUnavailableAsync(chain, address, purpose, report.Error.Message, now, cancellationToken);
        }

        var value = report.Value;
        var decision = Decide(value, policy);

        var screening = AddressScreening.Completed(
            chain, address, purpose, provider.Name, decision, value.Score, value.RiskLevel, value.Indicators,
            value.AddressLabel, value.ReportUrl, value.RawResponse, policy.Describe(), now,
            TimeSpan.FromDays(Math.Max(1, policy.CacheDays)));

        await repository.AddAsync(screening, cancellationToken);

        if (decision is ScreeningDecision.Block or ScreeningDecision.Review)
        {
            logger.LogWarning(
                "Address screening returned {Decision} for {Chain} {Address} (score {Score}, {Level}): {Reasons}",
                decision, chain, Mask(address), value.Score, value.RiskLevel, string.Join(", ", value.Indicators));
        }

        return ToVerdict(screening, fromCache: false);
    }

    public async Task<ScreeningVerdict?> FindLatestAsync(
        Chain chain, string address, CancellationToken cancellationToken = default)
    {
        var existing = await repository.FindLatestAsync(chain, address, cancellationToken);
        return existing is null ? null : ToVerdict(existing, fromCache: true);
    }

    public async Task<IReadOnlyList<string>> FindAddressesNeedingScreeningAsync(
        Chain chain,
        IReadOnlyCollection<string> addresses,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || addresses.Count == 0)
        {
            return [];
        }

        // With screening switched off, nothing needs screening. Returning the candidates instead would send
        // a caller off to screen a list that every call would answer Unavailable for, writing an evidence
        // row per address for no information.
        if (!_options.Enabled)
        {
            return [];
        }

        var fresh = await repository.FindFreshlyScreenedAsync(
            chain, addresses, clock.GetUtcNow(), cancellationToken);

        var freshSet = new HashSet<string>(fresh, StringComparer.OrdinalIgnoreCase);

        return [.. addresses.Where(a => !freshSet.Contains(a)).Take(limit)];
    }

    /// <summary>
    /// Our policy, applied to the vendor's numbers. The designation override is checked FIRST and
    /// independently of the score: a sanctions designation is a legal fact, so it must not be possible for
    /// a threshold change to let one through on a low score.
    ///
    /// <para><b>It matches Designations, not Indicators, and the distinction is load-bearing.</b> Vendors
    /// reuse the same risk-type codes for an address that IS designated and for one merely connected to a
    /// designated party several hops away. Matching the full indicator list refuses ordinary addresses on
    /// the strength of a counterparty's counterparty — verified against live data, where an address the
    /// vendor scores 3 out of 100 still carries a sanctioned_entity entry at three hops. Indirect exposure
    /// is a gradient the vendor already reflects in the score, so the thresholds below handle it; only a
    /// direct designation may override them.</para>
    /// </summary>
    private static ScreeningDecision Decide(AddressRiskReport report, ScreeningPolicy policy)
    {
        foreach (var indicator in report.Designations)
        {
            if (policy.AlwaysBlockIndicators.Any(
                    blocked => string.Equals(blocked, indicator, StringComparison.OrdinalIgnoreCase)))
                return ScreeningDecision.Block;
        }

        if (report.Score >= policy.BlockScore) return ScreeningDecision.Block;
        if (report.Score >= policy.ReviewScore) return ScreeningDecision.Review;

        // Proximity rule, off unless configured. It can only RAISE a clean result to Review — the two
        // checks above have already returned anything the score alone condemns, so reaching here means the
        // score said Allow and the question is whether closeness alone warrants a person looking.
        return IsCloseEnoughToReview(report, policy) ? ScreeningDecision.Review : ScreeningDecision.Allow;
    }

    /// <summary>
    /// Whether an INDIRECT exposure is near enough, and heavy enough, to be worth a human look.
    ///
    /// <para>Both conditions must hold. Distance alone would flag almost every address with exchange
    /// history, since nearly all of them sit a few hops from something bad at a trivial share of volume —
    /// which is the failure this rule exists to avoid, not to cause. Weight alone would flag an address
    /// whose entire history traces back to something bad at fifteen removes, which says more about the
    /// shape of the network than about this address.</para>
    ///
    /// <para>Direct findings are excluded because they are already handled above, and more harshly: a
    /// designation blocks outright.</para>
    /// </summary>
    private static bool IsCloseEnoughToReview(AddressRiskReport report, ScreeningPolicy policy)
    {
        if (!policy.ProximityRuleEnabled)
        {
            return false;
        }

        foreach (var exposure in report.Exposures)
        {
            if (exposure.IsDirect
                || exposure.Hops > policy.IndirectReviewMaxHops
                || exposure.Percent < policy.IndirectReviewMinPercent)
            {
                continue;
            }

            var matches = policy.AlwaysBlockIndicators.Any(
                blocked => string.Equals(blocked, exposure.RiskType, StringComparison.OrdinalIgnoreCase));

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ScreeningVerdict> RecordUnavailableAsync(
        Chain chain, string address, ScreeningPurpose purpose, string reason, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Resolved here too, so an Unavailable row is stamped with the same policy text a completed one
        // would carry. Two different renderings of the policy in one evidence table would make the trail
        // harder to read than no rendering at all. The provider caches, so this is not a read per failure.
        var policy = await policyProvider.GetAsync(cancellationToken);

        var screening = AddressScreening.Unavailable(
            chain, address, purpose, provider.Name, reason, policy.Describe(), now);
        await repository.AddAsync(screening, cancellationToken);
        return ToVerdict(screening, fromCache: false);
    }

    private static ScreeningVerdict ToVerdict(AddressScreening screening, bool fromCache) =>
        new(screening.Id, screening.Decision, screening.Score, screening.RiskLevel, screening.Reasons(),
            screening.AddressLabel, screening.ReportUrl, screening.ScreenedAt, fromCache);

    /// <summary>Addresses are public data, but a full destination in a log line is still needlessly
    /// re-identifying, so logs carry only the ends (§10 keeps full PII out of logs).</summary>
    private static string Mask(string address) =>
        address.Length <= 12 ? address : $"{address[..6]}…{address[^4..]}";
}
