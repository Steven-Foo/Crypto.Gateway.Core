using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

/// <summary><paramref name="Code"/> is the authenticator code, or a recovery code. Optional in the SHAPE
/// only: an enrolled account is refused without it. Nullable because an account that has not finished
/// enrolling has no code to give, and blocking its login would leave it unable to ever enroll.</summary>
public sealed record MerchantLoginCommand(string Username, string Password, string? Code = null);

/// <param name="TwoFactorEnrolled">False ⇒ the session is restricted to the enrollment endpoints.</param>
public sealed record MerchantLoginResult(
    string Token, string CsrfToken, DateTimeOffset ExpiresAt, Guid MerchantId, string Username, string DisplayName,
    IReadOnlyList<string> Permissions, bool MustChangePassword, bool TwoFactorEnrolled,
    MerchantTwoFactorMethod? TwoFactorMethod);

/// <summary>
/// A validated merchant-portal session. <see cref="MerchantId"/> is the tenant scope every portal endpoint must
/// filter by — it comes from here (the session), never from the request. Permissions are snapshotted at login.
/// </summary>
/// <param name="TwoFactorEnrolled">False ⇒ this session may reach only the enrollment endpoints. Enforced
/// by the host middleware, not this record.</param>
public sealed record MerchantPrincipal(
    Guid MerchantUserId, Guid MerchantId, string Username, string DisplayName, IReadOnlyList<string> Permissions,
    string CsrfToken, bool TwoFactorEnrolled);

public interface IMerchantAuthService
{
    Task<Result<MerchantLoginResult>> LoginAsync(MerchantLoginCommand command, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session behind this raw token. Idempotent — logging out twice is not an error.</summary>
    Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>Lifts the enrollment restriction on this session, once its owner has confirmed enrollment
    /// with a real authenticator code — so they land in the portal rather than back on a login form.</summary>
    Task<Result> CompleteEnrollmentForSessionAsync(string rawToken, CancellationToken cancellationToken = default);
}

public interface IMerchantSessionValidator
{
    Task<Result<MerchantPrincipal>> ValidateAsync(string rawToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Login/logout/session-validation for the merchant portal — the same server-side-opaque-token model as
/// <c>Platform.Identity.StaffAuthService</c>, but each session is bound to one <c>MerchantId</c> (the tenant).
///
/// <para>The account's <see cref="MerchantRole"/> permission set is resolved once, at login, and snapshotted
/// onto the session — so a role change takes effect at the user's next login, not mid-session (exactly the
/// staff module's trade-off, and why it avoids a second lookup on every authenticated request). An account
/// with no role resolves to an EMPTY set: fail-closed, never an implicit grant.</para>
/// </summary>
public sealed class MerchantAuthService(
    IMerchantUserRepository userRepository,
    IMerchantUserSessionRepository sessionRepository,
    IMerchantRoleRepository roleRepository,
    IMerchantDirectory merchants,
    IMerchantPasswordHasher passwordHasher,
    IMerchantSessionTokenGenerator tokenGenerator,
    IMerchantTwoFactorService twoFactor,
    IOptions<MerchantIdentityOptions> options,
    TimeProvider timeProvider) : IMerchantAuthService, IMerchantSessionValidator
{
    private readonly MerchantIdentityOptions _options = options.Value;

    public async Task<Result<MerchantLoginResult>> LoginAsync(
        MerchantLoginCommand command, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByUsernameAsync(command.Username.Trim(), cancellationToken);

        // Same error for "no such user" and "wrong password" — the response shape must not confirm whether a
        // username exists.
        if (user is null || !passwordHasher.Verify(command.Password, user.PasswordHash))
            return Result.Failure<MerchantLoginResult>(MerchantUserErrors.InvalidCredentials);

        if (!user.CanLogIn)
            return Result.Failure<MerchantLoginResult>(MerchantUserErrors.AccountDisabled);

        // Merchant-level shutout — distinct from the account's own status above. A Frozen merchant leaves this
        // untouched (its staff may still sign in); only Closed refuses. No merchant row at all is treated the
        // same as closed rather than allowed through by default.
        var merchant = await merchants.FindByIdAsync(user.MerchantId, cancellationToken);
        if (merchant is null || !merchant.CanAccessPortal)
            return Result.Failure<MerchantLoginResult>(MerchantUserErrors.MerchantClosed);

        // Checked AFTER the password (and the merchant-closed gate above), deliberately: reporting "wrong
        // code" to someone who also got the password wrong would confirm a valid username/password pair to
        // an attacker holding only those, and there is no reason to spend a 2FA check on a closed merchant.
        var factorResult = await ResolveSecondFactorAsync(user.Id, command.Code, cancellationToken);
        if (factorResult.IsFailure)
            return Result.Failure<MerchantLoginResult>(factorResult.Error!);

        // Resolve the tenant's role for this account. No role (or a role that has since been deleted) ⇒ no
        // permissions at all, so the user can sign in and see nothing rather than silently inheriting access.
        IReadOnlyList<string> permissions = [];
        if (user.RoleId is { } roleId)
        {
            var role = await roleRepository.FindByIdAsync(user.MerchantId, roleId, cancellationToken);
            permissions = role?.PermissionCodes ?? [];
        }

        var now = timeProvider.GetUtcNow();
        var token = tokenGenerator.Generate();
        var csrfToken = tokenGenerator.Generate().RawToken; // second CSPRNG value, compared plaintext (§ CsrfToken)
        var method = factorResult.Value;
        var session = MerchantUserSession.Issue(
            user.Id, user.MerchantId, user.Username, user.DisplayName, token.Hash, csrfToken, permissions,
            TimeSpan.FromHours(_options.SessionTtlHours), now, method);

        sessionRepository.Add(session);
        await sessionRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new MerchantLoginResult(
            token.RawToken, csrfToken, session.ExpiresAt, user.MerchantId, user.Username, user.DisplayName,
            permissions, user.MustChangePassword, TwoFactorEnrolled: method is not null, method));
    }

    /// <summary>
    /// Which factor proved this login, or null when the account has not enrolled.
    ///
    /// <para><b>An unenrolled account is allowed in, with a restricted session.</b> Refusing it would be a
    /// deadlock: enrollment happens over an authenticated session, so an account that cannot log in can
    /// never enroll. The host middleware confines such a session to the enrollment routes, which is what
    /// makes "every portal user is enrolled" a state rather than a policy someone has to remember.</para>
    ///
    /// <para>A six-digit value is tried as an authenticator code, anything else as a recovery code — a
    /// convenience, not a security boundary; both verify against stored material.</para>
    /// </summary>
    private async Task<Result<MerchantTwoFactorMethod?>> ResolveSecondFactorAsync(
        Guid merchantUserId, string? code, CancellationToken cancellationToken)
    {
        if (!await twoFactor.IsEnrolledAsync(merchantUserId, cancellationToken))
            return Result.Success<MerchantTwoFactorMethod?>(null);

        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<MerchantTwoFactorMethod?>(MerchantTwoFactorErrors.CodeRequired);

        var normalized = code.Replace(" ", "").Replace("-", "").Trim();
        var looksLikeTotp = normalized.Length == 6 && normalized.All(char.IsAsciiDigit);

        if (looksLikeTotp)
        {
            var verified = await twoFactor.VerifyAsync(merchantUserId, normalized, cancellationToken);
            return verified.IsFailure
                ? Result.Failure<MerchantTwoFactorMethod?>(verified.Error!)
                : Result.Success<MerchantTwoFactorMethod?>(MerchantTwoFactorMethod.Totp);
        }

        var redeemed = await twoFactor.RedeemRecoveryCodeAsync(merchantUserId, normalized, cancellationToken);
        return redeemed.IsFailure
            ? Result.Failure<MerchantTwoFactorMethod?>(redeemed.Error!)
            : Result.Success<MerchantTwoFactorMethod?>(MerchantTwoFactorMethod.RecoveryCode);
    }

    public async Task<Result> CompleteEnrollmentForSessionAsync(
        string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null || !session.IsValid(timeProvider.GetUtcNow()))
            return Result.Failure(MerchantUserErrors.SessionExpiredOrRevoked);

        if (!await twoFactor.IsEnrolledAsync(session.MerchantUserId, cancellationToken))
            return Result.Failure(MerchantTwoFactorErrors.NotEnrolled);

        session.MarkAuthenticatorProven();
        await sessionRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null)
            return Result.Success(); // already gone — logout is idempotent

        session.Revoke(timeProvider.GetUtcNow());
        await sessionRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<MerchantPrincipal>> ValidateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.FindByTokenHashAsync(tokenGenerator.HashOf(rawToken), cancellationToken);
        if (session is null || !session.IsValid(timeProvider.GetUtcNow()))
            return Result.Failure<MerchantPrincipal>(MerchantUserErrors.SessionExpiredOrRevoked);

        // Re-checked on every request, not just at login: closing a merchant must cut off an already-open
        // session immediately rather than waiting for it to expire naturally.
        var merchant = await merchants.FindByIdAsync(session.MerchantId, cancellationToken);
        if (merchant is null || !merchant.CanAccessPortal)
            return Result.Failure<MerchantPrincipal>(MerchantUserErrors.MerchantClosed);

        // Enrollment state comes from the session's recorded method, not a per-request lookup: a session
        // issued before enrollment stays restricted until it is upgraded in place by confirming.
        return Result.Success(new MerchantPrincipal(
            session.MerchantUserId, session.MerchantId, session.Username, session.DisplayName, session.PermissionCodes,
            session.CsrfToken, TwoFactorEnrolled: session.TwoFactorMethod is not null));
    }
}
