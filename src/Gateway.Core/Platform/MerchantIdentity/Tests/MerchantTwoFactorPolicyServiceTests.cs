using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Tests;

/// <summary>
/// Merchant-portal 2FA action policy, option 3: a platform minimum (staff) that every merchant is bound by and
/// cannot remove, plus each merchant's own additions on top. The load-bearing properties are that a merchant can
/// never end up LESS protected than the platform requires, that one merchant's additions never leak into another
/// tenant, and that "restore defaults" never means "nothing".
/// </summary>
public sealed class MerchantTwoFactorPolicyServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantTwoFactorPolicyTests";
    private static readonly Guid MerchantA = Guid.CreateVersion7();
    private static readonly Guid MerchantB = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantIdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<MerchantIdentityDbContext>().UseSqlServer(ConnectionString).Options);

    // One cache shared across a test, as the singleton is in a real host — so the tests also prove that a save
    // invalidates what a previous read cached.
    private readonly MerchantTwoFactorPolicyCache _cache = new();

    private MerchantTwoFactorPolicyService Service(
        MerchantIdentityDbContext c, MerchantTwoFactorPolicyOptions? options = null)
    {
        var repository = new MerchantTwoFactorPolicyRepository(c);
        var provider = new MerchantTwoFactorPolicyProvider(
            repository, Options.Create(options ?? new MerchantTwoFactorPolicyOptions()), _cache, TimeProvider.System);
        return new MerchantTwoFactorPolicyService(repository, provider, _cache, TimeProvider.System);
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
    public async Task With_nothing_saved_the_platform_minimum_is_the_recommended_baseline()
    {
        await using var c = Context();
        var service = Service(c);

        var platform = await service.GetPlatformAsync(Ct);
        platform.Current.Source.ShouldBe(MerchantTwoFactorPolicySource.Default);
        platform.Current.GuardedActions.ShouldBe(MerchantGuardedActions.RecommendedCodes, ignoreOrder: true);

        var merchant = await service.GetForMerchantAsync(MerchantA, Ct);
        merchant.PlatformRequired.ShouldBe(MerchantGuardedActions.RecommendedCodes, ignoreOrder: true);
        merchant.MerchantAdded.ShouldBeEmpty();
        merchant.IsGuarded(MerchantGuardedActions.PayoutCreate).ShouldBeTrue();
        merchant.IsGuarded(MerchantGuardedActions.TopUpCreate).ShouldBeFalse(); // not recommended
        merchant.IsGuarded(MerchantGuardedActions.TwoFactorPolicy).ShouldBeTrue(); // always on
    }

    [Fact]
    public async Task A_merchant_can_add_an_action_on_top_of_the_platform_minimum()
    {
        await using var c = Context();
        var service = Service(c);
        await service.GetForMerchantAsync(MerchantA, Ct); // warm the cache — the save must invalidate it

        var saved = await service.SaveForMerchantAsync(
            MerchantA, [MerchantGuardedActions.TopUpCreate], "stricter", "merchant.admin", Ct);

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.MerchantAdded.ShouldBe([MerchantGuardedActions.TopUpCreate]);
        saved.Value.IsGuarded(MerchantGuardedActions.TopUpCreate).ShouldBeTrue();
        saved.Value.IsGuarded(MerchantGuardedActions.PayoutCreate).ShouldBeTrue(); // minimum still applies
        saved.Value.MerchantSource.ShouldBe(MerchantTwoFactorPolicySource.Stored);
    }

    /// <summary>The whole point of option 3: a merchant sending a list that leaves out a platform-required
    /// action does NOT switch it off.</summary>
    [Fact]
    public async Task A_merchant_cannot_remove_a_platform_required_action()
    {
        await using var c = Context();
        var service = Service(c);

        var saved = await service.SaveForMerchantAsync(MerchantA, [], null, "merchant.admin", Ct);

        saved.IsSuccess.ShouldBeTrue();
        foreach (var required in MerchantGuardedActions.RecommendedCodes)
            saved.Value.IsGuarded(required).ShouldBeTrue();
    }

    [Fact]
    public async Task Platform_required_actions_are_never_stored_as_merchant_additions()
    {
        await using var c = Context();
        var service = Service(c);

        var saved = await service.SaveForMerchantAsync(
            MerchantA,
            [MerchantGuardedActions.PayoutCreate, MerchantGuardedActions.TopUpCreate, MerchantGuardedActions.TwoFactorPolicy],
            null, "merchant.admin", Ct);

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.MerchantAdded.ShouldBe([MerchantGuardedActions.TopUpCreate]);
    }

    [Fact]
    public async Task An_unknown_action_is_refused_and_nothing_is_saved()
    {
        await using var c = Context();
        var service = Service(c);

        var merchant = await service.SaveForMerchantAsync(MerchantA, ["ops.balances.adjust"], null, "merchant.admin", Ct);
        merchant.IsFailure.ShouldBeTrue();
        merchant.Error.ShouldBe(MerchantTwoFactorPolicyErrors.UnknownAction);

        var platform = await service.SavePlatformAsync(["portal.no-such-thing"], null, "staff", Ct);
        platform.IsFailure.ShouldBeTrue();

        (await c.MerchantTwoFactorPolicyVersions.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task One_merchants_additions_never_apply_to_another_merchant()
    {
        await using var c = Context();
        var service = Service(c);

        await service.SaveForMerchantAsync(MerchantA, [MerchantGuardedActions.TopUpCreate], null, "a.admin", Ct);

        var b = await service.GetForMerchantAsync(MerchantB, Ct);
        b.IsGuarded(MerchantGuardedActions.TopUpCreate).ShouldBeFalse();
        b.MerchantAdded.ShouldBeEmpty();

        (await service.GetHistoryAsync(MerchantB, 50, Ct)).ShouldBeEmpty();
        (await service.GetHistoryAsync(MerchantA, 50, Ct)).Count.ShouldBe(1);
        (await service.GetHistoryAsync(null, 50, Ct)).ShouldBeEmpty(); // platform history is its own layer
    }

    [Fact]
    public async Task A_platform_change_reaches_every_merchant_immediately_on_the_saving_host()
    {
        await using var c = Context();
        var service = Service(c);
        (await service.GetForMerchantAsync(MerchantA, Ct)).IsGuarded(MerchantGuardedActions.TopUpCreate).ShouldBeFalse();

        var saved = await service.SavePlatformAsync(
            [MerchantGuardedActions.PayoutCreate, MerchantGuardedActions.TopUpCreate], "tighten", "staff.admin", Ct);

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.Source.ShouldBe(MerchantTwoFactorPolicySource.Stored);

        var a = await service.GetForMerchantAsync(MerchantA, Ct);
        a.IsGuarded(MerchantGuardedActions.TopUpCreate).ShouldBeTrue();
        // The platform relaxed everything else — merchants follow, since the minimum is only a minimum.
        a.IsGuarded(MerchantGuardedActions.RolesManage).ShouldBeFalse();
    }

    /// <summary>If the platform starts requiring an action a merchant had already added, it moves to the locked
    /// column rather than appearing twice.</summary>
    [Fact]
    public async Task An_addition_later_required_by_the_platform_moves_to_the_locked_list()
    {
        await using var c = Context();
        var service = Service(c);
        await service.SaveForMerchantAsync(MerchantA, [MerchantGuardedActions.TopUpCreate], null, "a.admin", Ct);

        await service.SavePlatformAsync(
            [.. MerchantGuardedActions.RecommendedCodes, MerchantGuardedActions.TopUpCreate], null, "staff.admin", Ct);

        var a = await service.GetForMerchantAsync(MerchantA, Ct);
        a.PlatformRequired.ShouldContain(MerchantGuardedActions.TopUpCreate);
        a.MerchantAdded.ShouldNotContain(MerchantGuardedActions.TopUpCreate);
    }

    [Fact]
    public async Task Restore_defaults_returns_to_the_baseline_never_to_nothing()
    {
        await using var c = Context();
        var service = Service(c);

        await service.SavePlatformAsync([], "all off", "staff.admin", Ct);
        (await service.GetForMerchantAsync(MerchantA, Ct)).PlatformRequired.ShouldBeEmpty();

        var restored = await service.RestorePlatformDefaultsAsync(null, "staff.admin", Ct);
        restored.Value.GuardedActions.ShouldBe(MerchantGuardedActions.RecommendedCodes, ignoreOrder: true);
        restored.Value.Note.ShouldBe("Restored the recommended security baseline.");

        await service.SaveForMerchantAsync(MerchantA, [MerchantGuardedActions.TopUpCreate], null, "a.admin", Ct);
        var merchantRestored = await service.RestoreMerchantDefaultsAsync(MerchantA, null, "a.admin", Ct);
        merchantRestored.Value.MerchantAdded.ShouldBeEmpty();
        merchantRestored.Value.PlatformRequired.ShouldBe(MerchantGuardedActions.RecommendedCodes, ignoreOrder: true);
    }

    [Fact]
    public async Task Configuration_can_override_the_default_platform_minimum()
    {
        await using var c = Context();
        var service = Service(c, new MerchantTwoFactorPolicyOptions
        {
            PlatformGuardedActions = [MerchantGuardedActions.PayoutCreate, "portal.unknown-code"],
        });

        var platform = await service.GetPlatformAsync(Ct);
        platform.Current.GuardedActions.ShouldBe([MerchantGuardedActions.PayoutCreate]); // unknown code dropped
        platform.RecommendedDefaults.ShouldBe(MerchantGuardedActions.RecommendedCodes, ignoreOrder: true);
    }

    [Fact]
    public void The_audit_reason_always_fits_the_audit_column()
    {
        var everything = MerchantGuardedActions.Guardable.Select(a => a.Code).ToList();
        MerchantTwoFactorPolicyAudit.Describe([], everything, new string('x', 512)).Length
            .ShouldBeLessThanOrEqualTo(MerchantTwoFactorPolicyAudit.ReasonMaxLength);
    }
}
