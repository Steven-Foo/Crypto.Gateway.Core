using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

/// <summary>
/// <paramref name="Code"/> is the authenticator code (or a recovery code). It is optional in the SHAPE only
/// — an account holding an active factor is refused without it. The field is nullable because an account
/// that has not finished enrolling has no code to give, and blocking its login would leave it permanently
/// unable to enroll (§ forced enrollment).
/// </summary>
public sealed record LoginCommand(string Username, string Password, string? Code = null);

/// <param name="TwoFactorEnrolled">False when the account still has to enroll. The session that was issued
/// is RESTRICTED to the enrollment endpoints, so the SPA should route straight to the QR screen rather than
/// discovering the restriction one failed call at a time.</param>
/// <param name="TwoFactorMethod">Which factor proved the session — an authenticator, or a recovery code that
/// signs in but cannot authorise a guarded action.</param>
public sealed record LoginResult(
    string Token, string CsrfToken, DateTimeOffset ExpiresAt, string Username, Guid RoleId, string RoleName,
    IReadOnlyList<string> Permissions, bool TwoFactorEnrolled, TwoFactorMethod? TwoFactorMethod);

/// <summary>A validated bearer session — what the host middleware needs to authorize a request. Permissions
/// are exactly what was snapshotted onto the session at login (§ StaffSession) — re-resolving them from the
/// live Role on every request would defeat the point of snapshotting. <see cref="Username"/> is plain data a
/// caller (e.g. the audit log) can attribute an action to, without depending on Identity itself (§4.5).</summary>
/// <param name="TwoFactorEnrolled">False ⇒ this session is restricted to the enrollment endpoints (§4 of
/// docs/two-factor-authentication.md). The host middleware enforces that, not this record.</param>
/// <param name="AuthenticatorProven">True only when an authenticator code proved the session. A guarded
/// action requires it; a recovery-code session is refused.</param>
public sealed record StaffPrincipal(
    Guid StaffUserId, string Username, Guid RoleId, string RoleName, IReadOnlyList<string> Permissions, string CsrfToken,
    bool TwoFactorEnrolled, bool AuthenticatorProven);

public interface IStaffAuthService
{
    Task<Result<LoginResult>> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session behind this raw token. Idempotent — logging out twice is not an error.</summary>
    Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lifts the enrollment restriction on the session behind this token, once its owner has just confirmed
    /// enrollment with a real authenticator code.
    ///
    /// <para>Upgrading in place rather than forcing a re-login is deliberate: the alternative drops a
    /// brand-new user back on a login form the instant setup succeeds, to type a code from an app they have
    /// only just added — which is exactly where people conclude the setup failed.</para>
    /// </summary>
    Task<Result> CompleteEnrollmentForSessionAsync(string rawToken, CancellationToken cancellationToken = default);
}

public interface IStaffSessionValidator
{
    Task<Result<StaffPrincipal>> ValidateAsync(string rawToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Login/logout/session-validation in one class — they share the same small dependency set and none of
/// them is complex enough alone to earn its own file the way Create/Fail did for PaymentIntent.
/// </summary>
public sealed class StaffAuthService(
    IStaffUserRepository userRepository,
    IStaffSessionRepository sessionRepository,
    IRoleRepository roleRepository,
    IStaffPasswordHasher passwordHasher,
    IBearerTokenGenerator tokenGenerator,
    ITwoFactorService twoFactor,
    IOptions<StaffAuthOptions> options,
    TimeProvider timeProvider) : IStaffAuthService, IStaffSessionValidator
{
    private readonly StaffAuthOptions _options = options.Value;

    public async Task<Result<LoginResult>> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByUsernameAsync(command.Username.Trim(), cancellationToken);

        // Same error for "no such user" and "wrong password" — don't let the response shape confirm
        // whether a username exists.
        if (user is null || !passwordHasher.Verify(command.Password, user.PasswordHash))
            return Result.Failure<LoginResult>(StaffUserErrors.InvalidCredentials);

        if (!user.CanLogIn)
            return Result.Failure<LoginResult>(StaffUserErrors.AccountDisabled);

        // The second factor is checked AFTER the password, deliberately. Checking it first, or reporting
        // "wrong code" to someone who also got the password wrong, would confirm a valid username/password
        // pair to an attacker holding only those.
        var factorResult = await ResolveSecondFactorAsync(user.Id, command.Code, cancellationToken);
        if (factorResult.IsFailure)
            return Result.Failure<LoginResult>(factorResult.Error!);

        // The role's permission set is resolved here, once, and snapshotted onto the session — a role's
        // permissions changing takes effect on the account's next login, not mid-session (§ StaffSession).
        var role = await roleRepository.GetByIdAsync(user.RoleId, cancellationToken);
        if (role is null)
            return Result.Failure<LoginResult>(RoleErrors.NotFound);

        var now = timeProvider.GetUtcNow();
        var token = tokenGenerator.Generate();
        // A second CSPRNG value as the anti-CSRF token — same 256-bit generator, but we keep only the raw value
        // (it is compared plaintext, never a bearer credential, so it isn't hashed). See StaffSession.CsrfToken.
        var csrfToken = tokenGenerator.Generate().RawToken;
        var method = factorResult.Value;
        var session = StaffSession.Issue(
            user.Id, user.Username, token.Hash, csrfToken, role.Id, role.Name, role.PermissionCodes,
            TimeSpan.FromHours(_options.SessionTtlHours), now, method);

        sessionRepository.Add(session);
        await sessionRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new LoginResult(
            token.RawToken, csrfToken, session.ExpiresAt, user.Username, role.Id, role.Name, role.PermissionCodes,
            TwoFactorEnrolled: method is not null, method));
    }

    /// <summary>
    /// Which factor proved this login, or null when the account has not enrolled yet.
    ///
    /// <para><b>An unenrolled account is allowed in, and given a restricted session.</b> Refusing it outright
    /// would be more obviously "secure" and would also be a deadlock: enrollment happens over an
    /// authenticated session, so an account that cannot log in can never enroll, and a newly created staff
    /// member would need a second admin to do something no endpoint offers. The restriction is enforced by
    /// the host middleware, which lets such a session reach the enrollment routes and nothing else.</para>
    ///
    /// <para>A six-digit value is tried as an authenticator code; anything else as a recovery code. That
    /// shape test is a convenience, not a security boundary — both paths verify against stored material, and
    /// a wrong guess on either is the same refusal.</para>
    /// </summary>
    private async Task<Result<TwoFactorMethod?>> ResolveSecondFactorAsync(
        Guid staffUserId, string? code, CancellationToken cancellationToken)
    {
        if (!await twoFactor.IsEnrolledAsync(staffUserId, cancellationToken))
            return Result.Success<TwoFactorMethod?>(null);

        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<TwoFactorMethod?>(TwoFactorErrors.CodeRequired);

        var normalized = code.Replace(" ", "").Replace("-", "").Trim();
        var looksLikeTotp = normalized.Length == 6 && normalized.All(char.IsAsciiDigit);

        if (looksLikeTotp)
        {
            var verified = await twoFactor.VerifyAsync(staffUserId, normalized, cancellationToken);
            return verified.IsFailure
                ? Result.Failure<TwoFactorMethod?>(verified.Error!)
                : Result.Success<TwoFactorMethod?>(TwoFactorMethod.Totp);
        }

        var redeemed = await twoFactor.RedeemRecoveryCodeAsync(staffUserId, normalized, cancellationToken);
        return redeemed.IsFailure
            ? Result.Failure<TwoFactorMethod?>(redeemed.Error!)
            : Result.Success<TwoFactorMethod?>(TwoFactorMethod.RecoveryCode);
    }

    public async Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null)
            return Result.Success(); // already gone — logout is idempotent, not an error

        session.Revoke(timeProvider.GetUtcNow());
        await sessionRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> CompleteEnrollmentForSessionAsync(
        string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null || !session.IsValid(timeProvider.GetUtcNow()))
            return Result.Failure(StaffUserErrors.SessionExpiredOrRevoked);

        // Guarded by the enrollment state itself, not by trusting the caller: this only ever runs after the
        // service has verified a code against a now-active factor.
        if (!await twoFactor.IsEnrolledAsync(session.StaffUserId, cancellationToken))
            return Result.Failure(TwoFactorErrors.NotEnrolled);

        session.MarkAuthenticatorProven();
        await sessionRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<StaffPrincipal>> ValidateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null || !session.IsValid(timeProvider.GetUtcNow()))
            return Result.Failure<StaffPrincipal>(StaffUserErrors.SessionExpiredOrRevoked);

        // Enrollment state is read from the session's recorded method, not re-queried: a session issued
        // before enrollment stays restricted until it is upgraded in place by confirming (§ StaffSession),
        // which is what makes the restriction survive across requests without a per-request lookup.
        return Result.Success(new StaffPrincipal(
            session.StaffUserId, session.Username, session.RoleId, session.RoleName, session.PermissionCodes,
            session.CsrfToken, TwoFactorEnrolled: session.TwoFactorMethod is not null, session.AuthenticatorProven));
    }
}
