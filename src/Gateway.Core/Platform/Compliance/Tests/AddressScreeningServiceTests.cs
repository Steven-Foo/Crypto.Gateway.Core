using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Tests;

/// <summary>
/// Policy and caching behaviour. These are the rules that decide whether money moves, so they are tested
/// against the real service with an in-memory provider rather than against a mock of the service itself.
/// </summary>
public sealed class AddressScreeningServiceTests
{
    private const string Address = "TBTwgFxL4KwAzQmMAS2L13YHy58DW6zq7e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AddressScreeningService Service, InMemoryAddressRiskProvider Provider, InMemoryRepository Repo, TestClock Clock)
        Build(Action<ComplianceOptions>? configure = null)
    {
        var options = new ComplianceOptions { Enabled = true };
        configure?.Invoke(options);

        var provider = new InMemoryAddressRiskProvider();
        var repo = new InMemoryRepository();
        var clock = new TestClock();

        // The policy provider resolves thresholds from the store, falling back to config. These tests set
        // options directly and store nothing, so every case here runs on the configured values — which is
        // also the path a fresh environment takes before anyone saves a policy.
        var policyProvider = new ScreeningPolicyProvider(
            new NoStoredPolicy(), Options.Create(options), new ScreeningPolicyCache(), clock);

        var service = new AddressScreeningService(
            provider, repo, Options.Create(options), policyProvider, clock,
            NullLogger<AddressScreeningService>.Instance);

        return (service, provider, repo, clock);
    }

    [Fact]
    public async Task A_clean_address_is_allowed()
    {
        var (service, _, _, _) = Build();

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Allow);
        verdict.Score.ShouldBe(0);
    }

    [Fact]
    public async Task A_severe_score_blocks()
    {
        var (service, provider, _, _) = Build();
        provider.Stage(Chain.Tron, Address, 100, "Severe", "Malicious Address");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Block);
    }

    [Fact]
    public async Task A_high_score_is_held_for_review_not_blocked()
    {
        var (service, provider, _, _) = Build();
        provider.Stage(Chain.Tron, Address, 89, "High", "Mixer");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Review);
    }

    /// <summary>
    /// The load-bearing one. A sanctions designation is a legal fact, not a gradient, so it must block even
    /// when the numeric score sits in the Allow band. If this ever regresses, a threshold tweak silently
    /// starts letting sanctioned addresses through — which is the single worst failure this module can have.
    /// </summary>
    [Fact]
    public async Task A_sanctions_indicator_blocks_even_when_the_score_is_low()
    {
        var (service, provider, _, _) = Build();
        provider.Stage(Chain.Tron, Address, 5, "Low", "sanctioned_entity");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Block);
        verdict.Score.ShouldBe(5);
    }

    /// <summary>
    /// The counterpart to the test above, and the one that keeps screening usable. The same risk code
    /// reaches us for an address that IS designated and for one merely connected to a designated party
    /// several hops away. Live data confirms the latter is near-universal: an address the vendor scores 3
    /// out of 100 still carries a sanctioned_entity entry three hops out through an exchange. Blocking on
    /// that would refuse a large share of ordinary payout destinations, so indirect exposure is recorded as
    /// evidence and left to the score, which the thresholds already judge.
    /// </summary>
    [Fact]
    public async Task Indirect_sanctions_exposure_at_a_low_score_is_allowed_and_still_recorded()
    {
        var (service, provider, repo, _) = Build();
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", "sanctioned_entity", "illicit_activity");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Allow);

        // Allowed, but never hidden: the exposure stays on the evidence row for a human to read.
        repo.Rows.ShouldHaveSingleItem().Reasons().ShouldContain("sanctioned_entity");
    }

    [Fact]
    public async Task A_fresh_result_is_reused_instead_of_calling_the_provider_again()
    {
        var (service, provider, repo, clock) = Build(o => o.CacheDays = 30);
        provider.Stage(Chain.Tron, Address, 10, "Low");

        await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);
        clock.Advance(TimeSpan.FromDays(29));
        var second = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        second.FromCache.ShouldBeTrue();
        repo.Rows.Count.ShouldBe(1, "a cached hit must not append a second evidence row");
    }

    [Fact]
    public async Task A_stale_result_is_re_screened()
    {
        var (service, provider, repo, clock) = Build(o => o.CacheDays = 30);
        provider.Stage(Chain.Tron, Address, 10, "Low");

        await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);
        clock.Advance(TimeSpan.FromDays(31));
        var second = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        second.FromCache.ShouldBeFalse();
        repo.Rows.Count.ShouldBe(2, "re-screening appends rather than overwriting the earlier evidence");
    }

    /// <summary>
    /// A failed screening must never be cached as an answer. If it were, one provider outage would pin an
    /// address to "unknown" for the whole cache window, long after the provider recovered.
    /// </summary>
    [Fact]
    public async Task An_unavailable_result_is_never_reused_as_a_cached_answer()
    {
        var (service, _, repo, clock) = Build(o => o.Enabled = false);

        var first = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        first.Decision.ShouldBe(ScreeningDecision.Unavailable);
        second.FromCache.ShouldBeFalse();
        repo.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Screening_records_the_policy_that_was_in_force()
    {
        var (service, provider, repo, _) = Build(o => o.ReviewScore = 50);
        provider.Stage(Chain.Tron, Address, 60, "Moderate");

        await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        repo.Rows.Single().PolicyDescription.ShouldContain("review>=50");
    }

    [Fact]
    public async Task Disabled_screening_returns_unavailable_rather_than_allow()
    {
        var (service, _, _, _) = Build(o => o.Enabled = false);

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Unavailable);
        verdict.Decision.ShouldNotBe(ScreeningDecision.Allow);
    }

    /// <summary>
    /// The default. Everything that follows changes behaviour only because it was deliberately configured,
    /// so an existing deployment cannot start queueing payouts because someone shipped this code.
    /// </summary>
    [Fact]
    public async Task The_proximity_rule_is_off_unless_configured()
    {
        var (service, provider, _, _) = Build();
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 1, percent: 90m, "sanctioned_entity");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>
    /// The case the rule exists for: close, and a meaningful share of volume. Still only Review — Block
    /// stays reserved for a direct designation, which is a legal fact rather than a matter of degree.
    /// </summary>
    [Fact]
    public async Task Close_and_heavy_indirect_exposure_is_held_for_review()
    {
        var (service, provider, _, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 2;
            o.IndirectReviewMinPercent = 5m;
        });
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 1, percent: 60m, "sanctioned_entity");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Review);
    }

    /// <summary>
    /// The real live reading: 2.735% of volume, three hops out through an exchange. With the rule tuned to
    /// close, heavy exposure this must still pass, because nearly every address with exchange history looks
    /// like this and flagging them all is the failure the rule exists to avoid.
    /// </summary>
    [Fact]
    public async Task The_ordinary_live_reading_still_passes_under_a_sensible_threshold()
    {
        var (service, provider, _, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 2;
            o.IndirectReviewMinPercent = 5m;
        });
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 3, percent: 2.735m, "sanctioned_entity");

        var verdict = await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        verdict.Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>Distance alone is not enough. A close exposure worth a fraction of a percent is a trace.</summary>
    [Fact]
    public async Task Close_but_negligible_exposure_is_not_reviewed()
    {
        var (service, provider, _, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 3;
            o.IndirectReviewMinPercent = 5m;
        });
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 1, percent: 0.1m, "sanctioned_entity");

        (await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct))
            .Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>Weight alone is not enough either. An address whose whole history traces to something bad
    /// fifteen removes away says more about the network than about the address.</summary>
    [Fact]
    public async Task Heavy_but_distant_exposure_is_not_reviewed()
    {
        var (service, provider, _, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 2;
            o.IndirectReviewMinPercent = 5m;
        });
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 15, percent: 95m, "sanctioned_entity");

        (await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct))
            .Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>The rule applies to the designation list, not to every risk type. A close, heavy exposure to
    /// something that would not block if direct does not get a human look either.</summary>
    [Fact]
    public async Task A_risk_type_outside_the_designation_list_is_unaffected()
    {
        var (service, provider, _, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 3;
            o.IndirectReviewMinPercent = 1m;
        });
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 1, percent: 90m, "illicit_activity");

        (await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct))
            .Decision.ShouldBe(ScreeningDecision.Allow);
    }

    /// <summary>The rule can only raise a clean result. A direct designation still blocks outright, and
    /// enabling proximity must never soften that into a Review.</summary>
    [Fact]
    public async Task A_direct_designation_still_blocks_when_the_proximity_rule_is_on()
    {
        var (service, provider, _, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 3;
            o.IndirectReviewMinPercent = 1m;
        });
        provider.Stage(Chain.Tron, Address, 5, "Low", "sanctioned_entity");

        (await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct))
            .Decision.ShouldBe(ScreeningDecision.Block);
    }

    /// <summary>The thresholds in force are stamped onto the evidence, so a decision taken under one setting
    /// stays explainable after the setting changes.</summary>
    [Fact]
    public async Task The_proximity_thresholds_are_recorded_on_the_evidence()
    {
        var (service, provider, repo, _) = Build(o =>
        {
            o.IndirectReviewMaxHops = 2;
            o.IndirectReviewMinPercent = 5m;
        });
        provider.StageIndirect(Chain.Tron, Address, 3, "Low", hops: 1, percent: 60m, "sanctioned_entity");

        await service.ScreenAsync(Chain.Tron, Address, ScreeningPurpose.PayoutDestination, Ct);

        repo.Rows.ShouldHaveSingleItem().PolicyDescription.ShouldContain("indirect_review<=2hops>=5pct");
    }

    /// <summary>No policy has ever been saved, so the configured defaults are in force — the state of a
    /// fresh environment, and the one these tests exercise.</summary>
    private sealed class NoStoredPolicy : IScreeningPolicyRepository
    {
        public Task<ScreeningPolicyVersion?> FindLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScreeningPolicyVersion?>(null);

        public Task<IReadOnlyList<ScreeningPolicyVersion>> ListAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScreeningPolicyVersion>>([]);

        public Task AddAsync(ScreeningPolicyVersion version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryRepository : IAddressScreeningRepository
    {
        public List<AddressScreening> Rows { get; } = [];

        public Task<AddressScreening?> FindLatestAsync(
            Chain chain, string address, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows
                .Where(r => r.Chain == chain && string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.ScreenedAt)
                .FirstOrDefault());

        public Task AddAsync(AddressScreening screening, CancellationToken cancellationToken = default)
        {
            Rows.Add(screening);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> FindFreshlyScreenedAsync(
            Chain chain, IReadOnlyCollection<string> addresses, DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. Rows
                    .Where(r => r.Chain == chain
                                && addresses.Contains(r.Address, StringComparer.OrdinalIgnoreCase)
                                && r.IsFreshAt(now))
                    .Select(r => r.Address)
                    .Distinct(StringComparer.OrdinalIgnoreCase)]);
    }
}
