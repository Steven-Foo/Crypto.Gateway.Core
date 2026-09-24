using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Tests;

/// <summary>
/// <see cref="MerchantUser.IsPrimary"/> — the merchant's original super-admin, set automatically on the
/// account created when the merchant had zero accounts, never reassigned, and the ONE account platform staff
/// may reset. Every other account is the merchant's own business (§ platform-side password reset is
/// primary-only by design, per the product decision this feature implements).
/// </summary>
public sealed class MerchantPrimaryAccountTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantPrimaryAccountTests";
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantIdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<MerchantIdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static MerchantAccountService Accounts(MerchantIdentityDbContext c) =>
        new(new MerchantUserRepository(c), new MerchantRoleRepository(c), new MerchantPasswordHasher(),
            new MerchantPasswordGenerator(), TimeProvider.System);

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
    public async Task The_first_account_created_for_a_merchant_is_automatically_primary()
    {
        await using var context = Context();
        var accounts = Accounts(context);

        await accounts.CreateAsync(Tenant, "first", "First", null, Ct);

        var primary = (await accounts.GetPrimaryAsync(Tenant, Ct)).Value;
        primary.ShouldNotBeNull();
        primary!.Username.ShouldBe("first");
        primary.IsPrimary.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_account_for_the_same_merchant_is_never_primary()
    {
        await using var context = Context();
        var accounts = Accounts(context);

        await accounts.CreateAsync(Tenant, "first", "First", null, Ct);
        await accounts.CreateAsync(Tenant, "second", "Second", null, Ct);

        var all = (await accounts.ListAsync(Tenant, Ct)).Value;
        all.Single(a => a.Username == "first").IsPrimary.ShouldBeTrue();
        all.Single(a => a.Username == "second").IsPrimary.ShouldBeFalse();

        // Exactly one primary — the database's filtered unique index is the real arbiter, this just confirms
        // the application-level computation agrees with it.
        (await accounts.GetPrimaryAsync(Tenant, Ct)).Value!.Username.ShouldBe("first");
    }

    [Fact]
    public async Task A_merchant_with_no_accounts_has_no_primary()
    {
        await using var context = Context();
        (await Accounts(context).GetPrimaryAsync(Guid.CreateVersion7(), Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task The_primary_account_cannot_be_disabled()
    {
        await using var context = Context();
        var accounts = Accounts(context);
        var primaryId = (await accounts.CreateAsync(Tenant, "boss", "Boss", null, Ct)).Value.MerchantUserId;
        // A second account so the pre-existing "last active account" guard doesn't fire first and mask the
        // primary-account guard this test is actually checking.
        await accounts.CreateAsync(Tenant, "teammate", "Teammate", null, Ct);

        // A different acting user too, so this isn't masked by the unrelated "cannot disable self" guard.
        var result = await accounts.SetStatusAsync(Tenant, primaryId, Guid.CreateVersion7(), active: false, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantUserErrors.CannotDisablePrimaryAccount.Code);
    }

    [Fact]
    public async Task A_non_primary_account_can_still_be_disabled_normally()
    {
        await using var context = Context();
        var accounts = Accounts(context);
        await accounts.CreateAsync(Tenant, "boss", "Boss", null, Ct);
        var teammateId = (await accounts.CreateAsync(Tenant, "teammate", "Teammate", null, Ct)).Value.MerchantUserId;

        (await accounts.SetStatusAsync(Tenant, teammateId, Guid.CreateVersion7(), active: false, Ct))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Staff_reset_succeeds_only_for_the_primary_account()
    {
        await using var context = Context();
        var accounts = Accounts(context);
        var primaryId = (await accounts.CreateAsync(Tenant, "boss", "Boss", null, Ct)).Value.MerchantUserId;
        var teammateId = (await accounts.CreateAsync(Tenant, "teammate", "Teammate", null, Ct)).Value.MerchantUserId;

        (await accounts.ResetPrimaryPasswordAsync(Tenant, primaryId, Ct)).IsSuccess.ShouldBeTrue();

        var refused = await accounts.ResetPrimaryPasswordAsync(Tenant, teammateId, Ct);
        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(MerchantUserErrors.OnlyPrimaryResettableByStaff.Code);
    }

    [Fact]
    public async Task The_portals_own_teammate_reset_is_unaffected_by_the_primary_restriction()
    {
        // ResetPasswordAsync (used by the merchant's own admin inside the portal) must stay unrestricted —
        // only the platform-staff path (ResetPrimaryPasswordAsync) is primary-only.
        await using var context = Context();
        var accounts = Accounts(context);
        await accounts.CreateAsync(Tenant, "boss", "Boss", null, Ct);
        var teammateId = (await accounts.CreateAsync(Tenant, "teammate", "Teammate", null, Ct)).Value.MerchantUserId;

        (await accounts.ResetPasswordAsync(Tenant, teammateId, Ct)).IsSuccess.ShouldBeTrue();
    }
}
