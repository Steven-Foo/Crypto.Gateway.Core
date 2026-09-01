using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Tests;

/// <summary>
/// The merchant-portal auth service on real SQL Server. The point of interest beyond the Ops equivalent is the
/// <b>tenant</b>: a session carries the user's <c>MerchantId</c>, and validation surfaces it — that is what
/// scopes every portal read. Also covers CSRF issuance, the don't-leak-username rule, and (Phase 2) that the
/// session's permission set comes from the account's role and is <b>empty when it has none</b>.
/// </summary>
public sealed class MerchantAuthServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeMerchantIdentityTests";
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static MerchantIdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<MerchantIdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static MerchantAuthService Service(MerchantIdentityDbContext context, TimeProvider? clock = null) =>
        new(new MerchantUserRepository(context), new MerchantUserSessionRepository(context),
            new MerchantRoleRepository(context), new MerchantPasswordHasher(), new MerchantSessionTokenGenerator(),
            Options.Create(new MerchantIdentityOptions { SessionTtlHours = 8 }), clock ?? TimeProvider.System);

    private static async Task<Guid> SeedRoleAsync(Guid merchantId, string name, params string[] permissions)
    {
        await using var context = Context();
        var role = MerchantRole.Create(merchantId, name, null, permissions, DateTimeOffset.UtcNow).Value;
        context.MerchantRoles.Add(role);
        await context.SaveChangesAsync(Ct);
        return role.Id;
    }

    private static async Task SeedUserAsync(Guid merchantId, string username, string password, Guid? roleId = null)
    {
        await using var context = Context();
        var user = MerchantUser.Create(
            merchantId, username, "Ops", new MerchantPasswordHasher().Hash(password), roleId,
            mustChangePassword: false, DateTimeOffset.UtcNow).Value;
        context.MerchantUsers.Add(user);
        await context.SaveChangesAsync(Ct);
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
    public async Task Login_issues_a_session_bound_to_the_users_tenant_and_validation_surfaces_it()
    {
        var roleId = await SeedRoleAsync(Tenant, "Administrator", MerchantRole.WildcardPermission);
        await SeedUserAsync(Tenant, "merchant001", "s3cret-password", roleId);

        await using var context = Context();
        var login = await Service(context).LoginAsync(new MerchantLoginCommand("merchant001", "s3cret-password"), Ct);

        login.IsSuccess.ShouldBeTrue();
        login.Value.MerchantId.ShouldBe(Tenant);          // the session is scoped to the user's merchant
        login.Value.Token.ShouldNotBeNullOrWhiteSpace();
        login.Value.CsrfToken.ShouldNotBeNullOrWhiteSpace();
        login.Value.CsrfToken.ShouldNotBe(login.Value.Token);
        login.Value.Permissions.ShouldContain(MerchantRole.WildcardPermission);

        await using var verify = Context();
        var validated = await Service(verify).ValidateAsync(login.Value.Token, Ct);
        validated.IsSuccess.ShouldBeTrue();
        validated.Value.MerchantId.ShouldBe(Tenant);      // the tenant scope every portal endpoint reads
        validated.Value.CsrfToken.ShouldBe(login.Value.CsrfToken);
        validated.Value.Permissions.ShouldContain(MerchantRole.WildcardPermission);
    }

    [Fact]
    public async Task The_sessions_permissions_are_exactly_the_roles_codes()
    {
        var roleId = await SeedRoleAsync(Tenant, "Finance", "portal.overview.view", "portal.transactions.view");
        await SeedUserAsync(Tenant, "finance01", "s3cret-password", roleId);

        await using var context = Context();
        var login = await Service(context).LoginAsync(new MerchantLoginCommand("finance01", "s3cret-password"), Ct);

        login.Value.Permissions.ShouldBe(["portal.overview.view", "portal.transactions.view"], ignoreOrder: true);
        login.Value.Permissions.ShouldNotContain("portal.payouts.create"); // never an implicit grant
    }

    [Fact]
    public async Task An_account_with_no_role_gets_no_permissions_at_all()
    {
        // Fail-closed: the account can sign in, but the portal gate will refuse every permissioned route.
        await SeedUserAsync(Tenant, "noroles01", "s3cret-password", roleId: null);

        await using var context = Context();
        var login = await Service(context).LoginAsync(new MerchantLoginCommand("noroles01", "s3cret-password"), Ct);

        login.IsSuccess.ShouldBeTrue();
        login.Value.Permissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_fail_identically()
    {
        await SeedUserAsync(Tenant, "merchant002", "correct-password");

        await using var context = Context();
        var wrongPassword = await Service(context).LoginAsync(new MerchantLoginCommand("merchant002", "wrong"), Ct);
        var noSuchUser = await Service(context).LoginAsync(new MerchantLoginCommand("nobody", "wrong"), Ct);

        wrongPassword.Error!.Code.ShouldBe(MerchantUserErrors.InvalidCredentials.Code);
        noSuchUser.Error!.Code.ShouldBe(MerchantUserErrors.InvalidCredentials.Code);
    }

    [Fact]
    public async Task A_disabled_account_cannot_log_in()
    {
        await SeedUserAsync(Tenant, "disabled01", "s3cret-password");

        await using (var setup = Context())
        {
            var user = await setup.MerchantUsers.SingleAsync(u => u.Username == "disabled01", Ct);
            user.SetStatus(MerchantUserStatus.Disabled);
            await setup.SaveChangesAsync(Ct);
        }

        await using var context = Context();
        var login = await Service(context).LoginAsync(new MerchantLoginCommand("disabled01", "s3cret-password"), Ct);
        login.Error!.Code.ShouldBe(MerchantUserErrors.AccountDisabled.Code);
    }

    [Fact]
    public async Task Logout_immediately_invalidates_the_session()
    {
        await SeedUserAsync(Tenant, "merchant003", "s3cret-password");

        await using var context = Context();
        var token = (await Service(context).LoginAsync(new MerchantLoginCommand("merchant003", "s3cret-password"), Ct)).Value.Token;

        await using (var logout = Context())
            (await Service(logout).LogoutAsync(token, Ct)).IsSuccess.ShouldBeTrue();

        await using var verify = Context();
        (await Service(verify).ValidateAsync(token, Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_session_fails_validation()
    {
        await SeedUserAsync(Tenant, "merchant004", "s3cret-password");

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var context = Context();
        var token = (await Service(context, clock).LoginAsync(new MerchantLoginCommand("merchant004", "s3cret-password"), Ct)).Value.Token;

        clock.Advance(TimeSpan.FromHours(9)); // past the 8h TTL

        await using var verify = Context();
        (await Service(verify, clock).ValidateAsync(token, Ct)).IsFailure.ShouldBeTrue();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
