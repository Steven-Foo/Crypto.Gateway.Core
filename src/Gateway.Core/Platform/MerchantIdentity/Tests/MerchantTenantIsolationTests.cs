using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Tests;

/// <summary>
/// The property the whole merchant portal rests on: <b>one tenant can never reach another's accounts or roles</b>,
/// even when it knows the exact id. Every service method takes the caller's merchant id and every repository
/// query filters on it, so a foreign id reads as "not found" rather than being actionable.
///
/// These tests deliberately act as merchant A while passing merchant B's ids — the shape a real cross-tenant
/// attack would take if the host ever accepted a merchant id from a request (it never does; the id comes from
/// the validated session).
/// </summary>
public sealed class MerchantTenantIsolationTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantTenantIsolationTests";
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantIdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<MerchantIdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static MerchantRoleService Roles(MerchantIdentityDbContext c) =>
        new(new MerchantRoleRepository(c), new MerchantUserRepository(c), TimeProvider.System);

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
    public async Task A_merchant_only_sees_its_own_roles_and_accounts()
    {
        await using var context = Context();
        (await Roles(context).CreateAsync(TenantA, "Finance", null, ["portal.overview.view"], Ct)).IsSuccess.ShouldBeTrue();
        (await Roles(context).CreateAsync(TenantB, "Support", null, ["portal.overview.view"], Ct)).IsSuccess.ShouldBeTrue();
        (await Accounts(context).CreateAsync(TenantA, "a-user", "A", null, Ct)).IsSuccess.ShouldBeTrue();
        (await Accounts(context).CreateAsync(TenantB, "b-user", "B", null, Ct)).IsSuccess.ShouldBeTrue();

        var rolesOfA = (await Roles(context).ListAsync(TenantA, Ct)).Value;
        var accountsOfA = (await Accounts(context).ListAsync(TenantA, Ct)).Value;

        rolesOfA.ShouldHaveSingleItem().Name.ShouldBe("Finance");
        accountsOfA.ShouldHaveSingleItem().Username.ShouldBe("a-user");
    }

    [Fact]
    public async Task Merchant_A_cannot_read_edit_or_delete_merchant_Bs_role_even_knowing_its_id()
    {
        await using var context = Context();
        var bRoleId = (await Roles(context).CreateAsync(TenantB, "B-Only", null, ["portal.overview.view"], Ct)).Value.RoleId;

        // Acting as A, using B's real role id — every path must refuse.
        (await Roles(context).UpdateAsync(TenantA, bRoleId, "Hijacked", null, Ct))
            .Error!.Code.ShouldBe(MerchantRoleErrors.NotFound.Code);
        (await Roles(context).SetPermissionsAsync(TenantA, bRoleId, ["*"], Ct))
            .Error!.Code.ShouldBe(MerchantRoleErrors.NotFound.Code);
        (await Roles(context).DeleteAsync(TenantA, bRoleId, Ct))
            .Error!.Code.ShouldBe(MerchantRoleErrors.NotFound.Code);

        // B's role is untouched.
        var bRoles = (await Roles(context).ListAsync(TenantB, Ct)).Value;
        bRoles.ShouldHaveSingleItem().Name.ShouldBe("B-Only");
    }

    [Fact]
    public async Task Merchant_A_cannot_disable_or_reset_merchant_Bs_account()
    {
        await using var context = Context();
        var bUserId = (await Accounts(context).CreateAsync(TenantB, "b-victim", "B", null, Ct)).Value.MerchantUserId;

        (await Accounts(context).SetStatusAsync(TenantA, bUserId, Guid.CreateVersion7(), active: false, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.NotFound.Code);
        (await Accounts(context).ResetPasswordAsync(TenantA, bUserId, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.NotFound.Code);
        (await Accounts(context).AssignRoleAsync(TenantA, bUserId, null, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.NotFound.Code);
    }

    [Fact]
    public async Task A_role_from_another_tenant_can_never_be_assigned()
    {
        await using var context = Context();
        var bRoleId = (await Roles(context).CreateAsync(TenantB, "B-Admin", null, ["*"], Ct)).Value.RoleId;
        var aUserId = (await Accounts(context).CreateAsync(TenantA, "a-user2", "A", null, Ct)).Value.MerchantUserId;

        // The dangerous case: A tries to grant its own user a role object belonging to B.
        (await Accounts(context).AssignRoleAsync(TenantA, aUserId, bRoleId, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.RoleNotInTenant.Code);

        // And at creation time too.
        (await Accounts(context).CreateAsync(TenantA, "a-user3", "A", bRoleId, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.RoleNotInTenant.Code);
    }

    [Fact]
    public async Task Two_merchants_may_each_have_a_role_of_the_same_name()
    {
        await using var context = Context();

        (await Roles(context).CreateAsync(TenantA, "Administrator", null, ["*"], Ct)).IsSuccess.ShouldBeTrue();
        (await Roles(context).CreateAsync(TenantB, "Administrator", null, ["*"], Ct)).IsSuccess.ShouldBeTrue();

        // ...but not twice within one tenant.
        (await Roles(context).CreateAsync(TenantA, "Administrator", null, ["*"], Ct))
            .Error!.Code.ShouldBe(MerchantRoleErrors.NameAlreadyExists.Code);
    }

    [Fact]
    public async Task A_role_still_assigned_to_an_account_cannot_be_deleted()
    {
        await using var context = Context();
        var roleId = (await Roles(context).CreateAsync(TenantA, "InUse", null, ["portal.overview.view"], Ct)).Value.RoleId;
        (await Accounts(context).CreateAsync(TenantA, "a-inuse", "A", roleId, Ct)).IsSuccess.ShouldBeTrue();

        (await Roles(context).DeleteAsync(TenantA, roleId, Ct)).Error!.Code.ShouldBe(MerchantRoleErrors.InUse.Code);
    }

    [Fact]
    public async Task The_last_active_account_and_your_own_account_cannot_be_disabled()
    {
        await using var context = Context();
        var soleUserId = (await Accounts(context).CreateAsync(TenantA, "a-sole", "A", null, Ct)).Value.MerchantUserId;

        // Self-disable is refused before the last-account rule is even reached.
        (await Accounts(context).SetStatusAsync(TenantA, soleUserId, soleUserId, active: false, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.CannotDisableSelf.Code);

        // Another admin cannot strand the tenant either.
        (await Accounts(context).SetStatusAsync(TenantA, soleUserId, Guid.CreateVersion7(), active: false, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.CannotDisableLastActiveAccount.Code);
    }

    [Fact]
    public async Task Changing_your_own_password_requires_the_current_one_and_a_long_enough_new_one()
    {
        await using var context = Context();
        var created = (await Accounts(context).CreateAsync(TenantA, "a-pw", "A", null, Ct)).Value;

        (await Accounts(context).ChangeOwnPasswordAsync(TenantA, created.MerchantUserId, "wrong", "a-long-enough-password", Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.CurrentPasswordIncorrect.Code);

        (await Accounts(context).ChangeOwnPasswordAsync(TenantA, created.MerchantUserId, created.TemporaryPassword, "short", Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.PasswordTooShort.Code);

        (await Accounts(context).ChangeOwnPasswordAsync(
            TenantA, created.MerchantUserId, created.TemporaryPassword, "a-long-enough-password", Ct)).IsSuccess.ShouldBeTrue();

        // The forced-change flag clears once the user sets their own password.
        var user = await context.MerchantUsers.AsNoTracking().SingleAsync(u => u.Id == created.MerchantUserId, Ct);
        user.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task A_username_is_globally_unique_across_tenants()
    {
        // Usernames resolve a tenant at login, so they cannot collide even across merchants.
        await using var context = Context();
        (await Accounts(context).CreateAsync(TenantA, "shared-name", "A", null, Ct)).IsSuccess.ShouldBeTrue();

        (await Accounts(context).CreateAsync(TenantB, "shared-name", "B", null, Ct))
            .Error!.Code.ShouldBe(MerchantUserErrors.UsernameAlreadyExists.Code);
    }
}
