using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Tests;

/// <summary>
/// The policy that decides which actions demand a code. The load-bearing test here is
/// <see cref="The_policy_action_can_never_be_unguarded"/>: if that rule fails, every other control in this
/// feature can be switched off from a browser tab.
/// </summary>
public sealed class TwoFactorPolicyServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeIdentityTwoFactorPolicyTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static IdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private const string TopUp = "ops.treasury.top-up";
    private const string Adjust = "ops.balances.adjust";

    /// <summary>A fresh cache per service, so one test's window cannot hide another's write.</summary>
    private static (TwoFactorPolicyService Service, TwoFactorPolicyProvider Provider) Build(
        IdentityDbContext context, TwoFactorOptions? options = null, TwoFactorPolicyCache? cache = null)
    {
        var repo = new TwoFactorPolicyRepository(context);
        var resolvedCache = cache ?? new TwoFactorPolicyCache();
        var provider = new TwoFactorPolicyProvider(
            repo, Options.Create(options ?? new TwoFactorOptions()), resolvedCache, TimeProvider.System);

        return (new TwoFactorPolicyService(
            repo, new StaffTwoFactorRepository(context), provider, resolvedCache, TimeProvider.System), provider);
    }

    public async ValueTask InitializeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
        await context.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var context = Context();
        await context.Database.EnsureDeletedAsync(Ct);
    }

    [Fact]
    public async Task With_nothing_saved_the_configured_defaults_are_in_force()
    {
        await using var context = Context();
        var (service, _) = Build(context, new TwoFactorOptions { GuardedActions = { TopUp } });

        var view = await service.GetAsync(Ct);

        view.Current.Source.ShouldBe(TwoFactorPolicySource.Configuration);
        view.Current.IsGuarded(TopUp).ShouldBeTrue();
        view.Current.IsGuarded(Adjust).ShouldBeFalse();
    }

    [Fact]
    public async Task Saving_a_version_takes_over_from_configuration_and_is_attributed()
    {
        await using var context = Context();
        var (service, _) = Build(context, new TwoFactorOptions { GuardedActions = { TopUp } });

        var saved = await service.SaveAsync([Adjust], "tightening controls", "alice", Ct);

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.Source.ShouldBe(TwoFactorPolicySource.Stored);
        saved.Value.UpdatedBy.ShouldBe("alice");
        saved.Value.Note.ShouldBe("tightening controls");

        // A stored version fully replaces the configured list rather than adding to it — otherwise an action
        // could never be un-guarded, and the settings screen would silently not do what it shows.
        saved.Value.IsGuarded(Adjust).ShouldBeTrue();
        saved.Value.IsGuarded(TopUp).ShouldBeFalse();

        var view = await service.GetAsync(Ct);
        view.Current.Source.ShouldBe(TwoFactorPolicySource.Stored);
        view.ConfiguredDefaults.IsGuarded(TopUp).ShouldBeTrue(); // the floor stays visible beside it
    }

    /// <summary>
    /// The self-protection invariant. Without it the control unlocks itself: anyone on a stolen admin session
    /// unticks every action and every code prompt disappears. Saving a list that omits the policy action must
    /// still leave it guarded.
    /// </summary>
    [Fact]
    public async Task The_policy_action_can_never_be_unguarded()
    {
        await using var context = Context();
        var (service, _) = Build(context);

        var saved = await service.SaveAsync([], note: null, updatedBy: "attacker", Ct);

        saved.Value.IsGuarded(TwoFactorPolicyVersion.SelfProtectingAction).ShouldBeTrue();
        (await service.GetAsync(Ct)).Current
            .IsGuarded(TwoFactorPolicyVersion.SelfProtectingAction).ShouldBeTrue();
    }

    /// <summary>Even a configuration file that omits it, or a version saved before the action existed, must
    /// not leave the settings screen unguarded — so the rule is applied on the READ path too.</summary>
    [Fact]
    public async Task The_policy_action_is_guarded_even_under_configuration_defaults()
    {
        await using var context = Context();
        var (service, _) = Build(context, new TwoFactorOptions());

        var view = await service.GetAsync(Ct);

        view.Current.Source.ShouldBe(TwoFactorPolicySource.Configuration);
        view.Current.IsGuarded(TwoFactorPolicyVersion.SelfProtectingAction).ShouldBeTrue();
    }

    [Fact]
    public async Task History_is_append_only_and_newest_first()
    {
        await using var context = Context();
        var (service, _) = Build(context);

        await service.SaveAsync([TopUp], "first", "alice", Ct);
        await service.SaveAsync([TopUp, Adjust], "second", "bob", Ct);

        var history = await service.GetHistoryAsync(50, Ct);

        // Nothing is updated in place: an action taken last month has to stay explainable against the policy
        // that was actually in force then.
        history.Count.ShouldBe(2);
        history[0].UpdatedBy.ShouldBe("bob");
        history[1].UpdatedBy.ShouldBe("alice");
    }

    [Fact]
    public async Task A_save_is_visible_immediately_despite_the_cache_window()
    {
        await using var context = Context();
        var cache = new TwoFactorPolicyCache();
        var (service, provider) = Build(context, cache: cache);

        // Warm the cache, then change the policy. Without the invalidate-after-commit the change would be
        // invisible on this host for the whole window — on its own settings screen.
        (await provider.GetAsync(Ct)).IsGuarded(TopUp).ShouldBeFalse();

        await service.SaveAsync([TopUp], null, "alice", Ct);

        (await provider.GetAsync(Ct)).IsGuarded(TopUp).ShouldBeTrue();
    }

    [Fact]
    public async Task An_unattributed_change_is_refused()
    {
        await using var context = Context();
        var (service, _) = Build(context);

        // An attribution the caller supplied is not an attribution; this comes from the validated session.
        var saved = await service.SaveAsync([TopUp], null, "   ", Ct);

        saved.IsFailure.ShouldBeTrue();
        saved.Error!.Code.ShouldBe(TwoFactorErrors.UpdatedByRequired.Code);

        // A refused update persists nothing.
        (await service.GetHistoryAsync(50, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_action_code_containing_the_separator_is_refused()
    {
        await using var context = Context();
        var (service, _) = Build(context);

        // Stored as a '|'-joined list, so a code containing one would silently split into two bogus actions.
        var saved = await service.SaveAsync(["ops.a|ops.b"], null, "alice", Ct);

        saved.Error!.Code.ShouldBe(TwoFactorErrors.InvalidActionCode.Code);
    }

    [Fact]
    public async Task Duplicate_and_blank_codes_are_normalised_away()
    {
        await using var context = Context();
        var (service, _) = Build(context);

        var saved = await service.SaveAsync([TopUp, " " + TopUp + " ", "", "   "], null, "alice", Ct);

        saved.Value.GuardedActions.Count(a => a == TopUp).ShouldBe(1);
        saved.Value.GuardedActions.ShouldNotContain("");
    }
}
