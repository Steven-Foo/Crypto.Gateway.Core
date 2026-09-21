using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Tests;

/// <summary>
/// The thresholds staff can set, and the rules about what they may not set.
///
/// <para>These decide whether money moves, so the guards here are the point: configuration is the floor a
/// system always has, a saved version overrides it, and the shipped sanctions designations cannot be
/// removed through the API no matter what is saved.</para>
/// </summary>
public sealed class ScreeningPolicyServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ScreeningPolicyService Service, InMemoryPolicyRepository Repo, ScreeningPolicyCache Cache)
        Build(Action<ComplianceOptions>? configure = null)
    {
        var options = new ComplianceOptions
        {
            Enabled = true,
            BlockScore = 91,
            ReviewScore = 71,
            CacheDays = 30,
        };
        configure?.Invoke(options);

        var repo = new InMemoryPolicyRepository();
        var cache = new ScreeningPolicyCache();
        var clock = new TestClock();

        var provider = new ScreeningPolicyProvider(repo, Options.Create(options), cache, clock);

        return (new ScreeningPolicyService(
            repo, provider, cache, clock, NullLogger<ScreeningPolicyService>.Instance), repo, cache);
    }

    private static ScreeningPolicyUpdate Update(
        int block = 80, int review = 60, int cacheDays = 14, int hops = 2, decimal percent = 5m,
        IReadOnlyList<string>? added = null, string? note = "tuned from measured data") =>
        new(block, review, cacheDays, hops, percent, added, note);

    /// <summary>
    /// A fresh environment has never had a policy saved. It must still have one — "no policy" on a control
    /// that decides whether money moves is the worst possible default.
    /// </summary>
    [Fact]
    public async Task With_nothing_saved_the_configured_defaults_are_in_force()
    {
        var (service, _, _) = Build();

        var policy = await service.GetAsync(Ct);

        policy.BlockScore.ShouldBe(91);
        policy.ReviewScore.ShouldBe(71);
        policy.Source.ShouldBe("Configuration");
        policy.UpdatedBy.ShouldBeNull();
    }

    /// <summary>
    /// "Nobody has set this" and "someone set it to exactly the default" look identical without the source,
    /// and only one of them is a question worth asking on a settings screen.
    /// </summary>
    [Fact]
    public async Task A_saved_policy_reports_itself_as_stored_even_when_it_matches_the_defaults()
    {
        var (service, _, _) = Build();

        await service.UpdateAsync(Update(block: 91, review: 71, cacheDays: 30, hops: 0, percent: 0m), "alice", Ct);

        var policy = await service.GetAsync(Ct);

        policy.Source.ShouldBe("Stored");
        policy.UpdatedBy.ShouldBe("alice");
    }

    [Fact]
    public async Task A_saved_policy_overrides_the_configured_values()
    {
        var (service, _, _) = Build();

        await service.UpdateAsync(Update(block: 80, review: 60, hops: 3, percent: 1.5m), "alice", Ct);

        var policy = await service.GetAsync(Ct);

        policy.BlockScore.ShouldBe(80);
        policy.ReviewScore.ShouldBe(60);
        policy.IndirectReviewMaxHops.ShouldBe(3);
        policy.IndirectReviewMinPercent.ShouldBe(1.5m);
    }

    /// <summary>The defaults stay visible beside the current values, so staff can see what they changed.</summary>
    [Fact]
    public async Task The_configured_defaults_remain_readable_after_an_override()
    {
        var (service, _, _) = Build();

        await service.UpdateAsync(Update(block: 80), "alice", Ct);

        service.GetConfiguredDefaults().BlockScore.ShouldBe(91);
        (await service.GetAsync(Ct)).BlockScore.ShouldBe(80);
    }

    /// <summary>
    /// The guard that matters most. A sanctions override is exactly the rule that should not come off in a
    /// web form, so saving a policy cannot remove what the platform ships with — only add to it.
    /// </summary>
    [Fact]
    public async Task Saving_a_policy_can_add_designations_but_never_remove_the_shipped_ones()
    {
        var (service, _, _) = Build();

        await service.UpdateAsync(Update(added: ["mixer"]), "alice", Ct);

        var policy = await service.GetAsync(Ct);

        policy.AlwaysBlockIndicators.ShouldContain("sanctioned_entity");
        policy.AlwaysBlockIndicators.ShouldContain("mixer");

        // Only what staff added may be taken away again.
        policy.EditableIndicators.ShouldBe(["mixer"]);
    }

    /// <summary>A review floor above the block floor means nothing ever reaches review, because anything that
    /// qualified was already refused. Silently useless is worse than refused.</summary>
    [Fact]
    public async Task A_review_threshold_above_the_block_threshold_is_refused()
    {
        var (service, repo, _) = Build();

        var result = await service.UpdateAsync(Update(block: 50, review: 80), "alice", Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("compliance.review_above_block");
        repo.Versions.ShouldBeEmpty("a refused update must persist nothing");
    }

    [Theory]
    [InlineData(0, 60, "compliance.invalid_score")]
    [InlineData(101, 60, "compliance.invalid_score")]
    public async Task A_score_outside_one_to_a_hundred_is_refused(int block, int review, string code)
    {
        var (service, _, _) = Build();

        var result = await service.UpdateAsync(Update(block: block, review: review), "alice", Ct);

        result.Error!.Code.ShouldBe(code);
    }

    /// <summary>Zero would re-screen every address on sight and exhaust the daily quota — the one setting
    /// that can turn a working control into an outage.</summary>
    [Fact]
    public async Task A_zero_cache_window_is_refused()
    {
        var (service, _, _) = Build();

        (await service.UpdateAsync(Update(cacheDays: 0), "alice", Ct))
            .Error!.Code.ShouldBe("compliance.invalid_cache_days");
    }

    /// <summary>Beyond about ten hops a finding describes the shape of the network rather than the address,
    /// so a larger limit only looks cautious.</summary>
    [Fact]
    public async Task An_absurd_hop_limit_is_refused()
    {
        var (service, _, _) = Build();

        (await service.UpdateAsync(Update(hops: 25), "alice", Ct))
            .Error!.Code.ShouldBe("compliance.invalid_hops");
    }

    [Fact]
    public async Task Zero_hops_is_accepted_because_it_disables_the_proximity_rule()
    {
        var (service, _, _) = Build();

        (await service.UpdateAsync(Update(hops: 0), "alice", Ct)).IsSuccess.ShouldBeTrue();
        (await service.GetAsync(Ct)).IndirectReviewMaxHops.ShouldBe(0);
    }

    [Fact]
    public async Task An_unattributed_change_is_refused()
    {
        var (service, _, _) = Build();

        (await service.UpdateAsync(Update(), "   ", Ct))
            .Error!.Code.ShouldBe("compliance.unattributed_change");
    }

    /// <summary>
    /// Append-only, like the evidence it governs. A payout allowed last month has to stay explainable
    /// against the thresholds that were actually in force, which a mutable settings row destroys.
    /// </summary>
    [Fact]
    public async Task Each_change_appends_a_version_rather_than_overwriting_one()
    {
        var (service, _, _) = Build();

        await service.UpdateAsync(Update(block: 90, note: "first"), "alice", Ct);
        await service.UpdateAsync(Update(block: 80, note: "second"), "bob", Ct);

        var history = await service.GetHistoryAsync(50, Ct);

        history.Count.ShouldBe(2);
        history[0].UpdatedBy.ShouldBe("bob", "newest first");
        history[0].Note.ShouldBe("second");
        history[1].UpdatedBy.ShouldBe("alice");
    }

    /// <summary>The cache is what stops a screening reading the database per address. It must not also stop
    /// a deliberate change from taking effect on the host that made it.</summary>
    [Fact]
    public async Task Saving_invalidates_the_cache_so_the_change_is_visible_at_once()
    {
        var (service, _, _) = Build();

        // Warm the cache on the configured defaults.
        (await service.GetAsync(Ct)).BlockScore.ShouldBe(91);

        await service.UpdateAsync(Update(block: 55, review: 40), "alice", Ct);

        (await service.GetAsync(Ct)).BlockScore.ShouldBe(55);
    }

    private sealed class InMemoryPolicyRepository : IScreeningPolicyRepository
    {
        public List<ScreeningPolicyVersion> Versions { get; } = [];

        public Task<ScreeningPolicyVersion?> FindLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Versions.LastOrDefault());

        public Task<IReadOnlyList<ScreeningPolicyVersion>> ListAsync(
            int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScreeningPolicyVersion>>(
                [.. Enumerable.Reverse(Versions).Take(limit)]);

        public Task AddAsync(ScreeningPolicyVersion version, CancellationToken cancellationToken = default)
        {
            Versions.Add(version);
            return Task.CompletedTask;
        }
    }
}
