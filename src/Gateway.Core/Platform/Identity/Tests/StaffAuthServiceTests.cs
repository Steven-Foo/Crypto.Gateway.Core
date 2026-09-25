using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Tests;

public sealed class StaffAuthServiceTests : IAsyncLifetime
{
    private const string DbName = "CpeIdentityTests";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("CPE_TEST_SQL") is { Length: > 0 } configured
            ? configured.Replace("{db}", DbName)
            : $@"Server=(localdb)\MSSQLLocalDB;Database={DbName};Trusted_Connection=True;TrustServerCertificate=True";

    private static IdentityDbContext Context() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(ConnectionString).Options);

    private static StaffAuthService Service(IdentityDbContext context, TimeProvider? timeProvider = null) =>
        new(new StaffUserRepository(context), new StaffSessionRepository(context), new RoleRepository(context),
            new StaffPasswordHasher(), new BearerTokenGenerator(), TwoFactor(context),
            Options.Create(new StaffAuthOptions { SessionTtlHours = 8 }),
            timeProvider ?? TimeProvider.System);

    /// <summary>The real service over the same context — these tests exercise accounts that have NOT
    /// enrolled, which is the forced-enrollment path: login succeeds and issues a restricted session.</summary>
    private static TwoFactorService TwoFactor(IdentityDbContext context) =>
        new(new StaffTwoFactorRepository(context),
            new AesGcmTwoFactorSecretCipher(Options.Create(new TwoFactorSecretOptions
            {
                CurrentKeyVersion = 1,
                Keys = { [1] = Convert.ToBase64String(Enumerable.Repeat((byte)0x2A, 32).ToArray()) },
            })),
            Options.Create(new TwoFactorOptions()),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TwoFactorService>.Instance);

    /// <summary>Seeds an Admin (wildcard) role once per test DB and returns its id — mirrors what
    /// <c>DevStaffSeeder</c> does in the real host.</summary>
    private static async Task<Guid> SeedAdminRoleAsync()
    {
        await using var context = Context();
        var existing = await context.Roles.SingleOrDefaultAsync(r => r.Name == "Admin", Ct);
        if (existing is not null)
            return existing.Id;

        var role = Role.Create("Admin", "Full access (test seed)", [Role.WildcardPermission], DateTimeOffset.UtcNow).Value;
        context.Roles.Add(role);
        await context.SaveChangesAsync(Ct);
        return role.Id;
    }

    private static async Task<Guid> SeedUserAsync(
        string username, string password, Guid? roleId = null, bool requireTwoFactor = true)
    {
        var resolvedRoleId = roleId ?? await SeedAdminRoleAsync();

        await using var context = Context();
        var user = StaffUser.Create(
            username, new StaffPasswordHasher().Hash(password), resolvedRoleId, requireTwoFactor, DateTimeOffset.UtcNow).Value;
        context.StaffUsers.Add(user);
        await context.SaveChangesAsync(Ct);
        return user.Id;
    }

    /// <summary>Enrolls and activates a real authenticator for an already-seeded account, returning the raw
    /// secret so a test can produce a valid code with <see cref="Totp.ComputeAt"/>.</summary>
    private static async Task<byte[]> EnrollAsync(Guid staffUserId, string username)
    {
        await using var context = Context();
        var service = TwoFactor(context);

        var enrollment = await service.BeginEnrollmentAsync(staffUserId, username, Ct);
        Totp.TryFromBase32(enrollment.Value.SecretBase32, out var secret).ShouldBeTrue();

        var confirm = await service.ConfirmEnrollmentAsync(
            staffUserId, Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow)), Ct);
        confirm.IsSuccess.ShouldBeTrue();

        return secret;
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
    public void Password_hasher_round_trips_and_rejects_a_wrong_password()
    {
        var hasher = new StaffPasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        hasher.Verify("correct horse battery staple", hash).ShouldBeTrue();
        hasher.Verify("wrong password", hash).ShouldBeFalse();
    }

    [Fact]
    public async Task Logging_in_with_valid_credentials_returns_a_usable_bearer_token()
    {
        await SeedUserAsync("admin1", "s3cret-password");

        await using var context = Context();
        var login = await Service(context).LoginAsync(new LoginCommand("admin1", "s3cret-password"), Ct);

        login.IsSuccess.ShouldBeTrue();
        login.Value.RoleName.ShouldBe("Admin");
        login.Value.Permissions.ShouldContain(Role.WildcardPermission);
        login.Value.Token.ShouldNotBeNullOrWhiteSpace();
        // A per-session anti-CSRF token is issued alongside the session token, and is a DISTINCT value.
        login.Value.CsrfToken.ShouldNotBeNullOrWhiteSpace();
        login.Value.CsrfToken.ShouldNotBe(login.Value.Token);

        await using var verify = Context();
        var validated = await Service(verify).ValidateAsync(login.Value.Token, Ct);
        validated.IsSuccess.ShouldBeTrue();
        validated.Value.RoleName.ShouldBe("Admin");
        validated.Value.Permissions.ShouldContain(Role.WildcardPermission);
        // Validation surfaces the same CSRF token the middleware compares the X-CSRF-Token header against.
        validated.Value.CsrfToken.ShouldBe(login.Value.CsrfToken);
    }

    [Fact]
    public async Task Logging_in_with_a_wrong_password_fails_without_revealing_whether_the_username_exists()
    {
        await SeedUserAsync("admin2", "correct-password");

        await using var context = Context();
        var wrongPassword = await Service(context).LoginAsync(new LoginCommand("admin2", "wrong"), Ct);
        var noSuchUser = await Service(context).LoginAsync(new LoginCommand("nobody", "wrong"), Ct);

        wrongPassword.Error!.Code.ShouldBe(StaffUserErrors.InvalidCredentials.Code);
        noSuchUser.Error!.Code.ShouldBe(StaffUserErrors.InvalidCredentials.Code);
    }

    [Fact]
    public async Task Logging_out_immediately_invalidates_the_token()
    {
        await SeedUserAsync("admin3", "s3cret-password");

        await using var context = Context();
        var token = (await Service(context).LoginAsync(new LoginCommand("admin3", "s3cret-password"), Ct)).Value.Token;

        await using (var logoutContext = Context())
            (await Service(logoutContext).LogoutAsync(token, Ct)).IsSuccess.ShouldBeTrue();

        await using var verify = Context();
        (await Service(verify).ValidateAsync(token, Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Logging_out_twice_is_not_an_error()
    {
        await SeedUserAsync("admin4", "s3cret-password");

        await using var context = Context();
        var token = (await Service(context).LoginAsync(new LoginCommand("admin4", "s3cret-password"), Ct)).Value.Token;

        await using (var first = Context()) (await Service(first).LogoutAsync(token, Ct)).IsSuccess.ShouldBeTrue();
        await using (var second = Context()) (await Service(second).LogoutAsync(token, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_session_fails_validation()
    {
        await SeedUserAsync("admin5", "s3cret-password");

        var fakeClock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var context = Context();
        var token = (await Service(context, fakeClock).LoginAsync(new LoginCommand("admin5", "s3cret-password"), Ct)).Value.Token;

        fakeClock.Advance(TimeSpan.FromHours(9)); // past the 8h TTL

        await using var verify = Context();
        (await Service(verify, fakeClock).ValidateAsync(token, Ct)).IsFailure.ShouldBeTrue();
    }

    // ── the live RequireTwoFactor switch (§ StaffUser.RequireTwoFactor) — four scenarios, independent of
    // whether the account has actually bound an authenticator ──

    [Fact]
    public async Task Switch_off_and_never_bound_logs_in_with_no_code_and_no_restriction()
    {
        await SeedUserAsync("switch.off.unbound", "s3cret-password", requireTwoFactor: false);

        await using var context = Context();
        var login = await Service(context).LoginAsync(new LoginCommand("switch.off.unbound", "s3cret-password"), Ct);

        login.IsSuccess.ShouldBeTrue();
        login.Value.TwoFactorEnrolled.ShouldBeTrue();
        login.Value.TwoFactorMethod.ShouldBeNull();

        await using var verify = Context();
        var validated = await Service(verify).ValidateAsync(login.Value.Token, Ct);
        validated.Value.TwoFactorEnrolled.ShouldBeTrue();
        validated.Value.AuthenticatorProven.ShouldBeTrue();
    }

    [Fact]
    public async Task Switch_on_and_never_bound_is_forced_into_a_restricted_session()
    {
        await SeedUserAsync("switch.on.unbound", "s3cret-password", requireTwoFactor: true);

        await using var context = Context();
        var login = await Service(context).LoginAsync(new LoginCommand("switch.on.unbound", "s3cret-password"), Ct);

        // Login still succeeds — refusing it outright would deadlock enrollment (§ ResolveSecondFactorAsync)
        // — but the session is not enrolled, which is what the host restricts to the enrollment routes.
        login.IsSuccess.ShouldBeTrue();
        login.Value.TwoFactorEnrolled.ShouldBeFalse();
        login.Value.TwoFactorMethod.ShouldBeNull();

        await using var verify = Context();
        var validated = await Service(verify).ValidateAsync(login.Value.Token, Ct);
        validated.Value.TwoFactorEnrolled.ShouldBeFalse();
        validated.Value.AuthenticatorProven.ShouldBeFalse();
    }

    [Fact]
    public async Task Switch_off_and_already_bound_still_logs_in_with_no_code_and_ignores_the_binding()
    {
        var staffUserId = await SeedUserAsync("switch.off.bound", "s3cret-password", requireTwoFactor: false);
        await EnrollAsync(staffUserId, "switch.off.bound");

        await using var context = Context();
        // No code supplied at all — a switch-off account must not need one, even though a factor exists.
        var login = await Service(context).LoginAsync(new LoginCommand("switch.off.bound", "s3cret-password"), Ct);

        login.IsSuccess.ShouldBeTrue();
        login.Value.TwoFactorEnrolled.ShouldBeTrue();
        // The binding is real but the switch is off, so nothing checked it this login — the session's
        // recorded method stays an honest null rather than a fabricated "Totp".
        login.Value.TwoFactorMethod.ShouldBeNull();
    }

    [Fact]
    public async Task Switch_on_and_already_bound_requires_a_code()
    {
        var staffUserId = await SeedUserAsync("switch.on.bound", "s3cret-password", requireTwoFactor: true);
        var secret = await EnrollAsync(staffUserId, "switch.on.bound");

        await using (var noCode = Context())
        {
            var refused = await Service(noCode).LoginAsync(new LoginCommand("switch.on.bound", "s3cret-password"), Ct);
            refused.IsFailure.ShouldBeTrue();
            refused.Error!.Code.ShouldBe(TwoFactorErrors.CodeRequired.Code);
        }

        await using var context = Context();
        var code = Totp.ComputeAt(secret, Totp.StepAt(DateTimeOffset.UtcNow));
        var login = await Service(context).LoginAsync(new LoginCommand("switch.on.bound", "s3cret-password", code), Ct);

        login.IsSuccess.ShouldBeTrue();
        login.Value.TwoFactorEnrolled.ShouldBeTrue();
        login.Value.TwoFactorMethod.ShouldBe(TwoFactorMethod.Totp);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
