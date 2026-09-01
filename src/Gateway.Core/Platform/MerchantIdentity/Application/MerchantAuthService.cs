using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

public sealed record MerchantLoginCommand(string Username, string Password);

public sealed record MerchantLoginResult(
    string Token, string CsrfToken, DateTimeOffset ExpiresAt, Guid MerchantId, string Username, string DisplayName,
    IReadOnlyList<string> Permissions, bool MustChangePassword);

/// <summary>
/// A validated merchant-portal session. <see cref="MerchantId"/> is the tenant scope every portal endpoint must
/// filter by — it comes from here (the session), never from the request. Permissions are snapshotted at login.
/// </summary>
public sealed record MerchantPrincipal(
    Guid MerchantUserId, Guid MerchantId, string Username, string DisplayName, IReadOnlyList<string> Permissions, string CsrfToken);

public interface IMerchantAuthService
{
    Task<Result<MerchantLoginResult>> LoginAsync(MerchantLoginCommand command, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session behind this raw token. Idempotent — logging out twice is not an error.</summary>
    Task<Result> LogoutAsync(string rawToken, CancellationToken cancellationToken = default);
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
    IMerchantPasswordHasher passwordHasher,
    IMerchantSessionTokenGenerator tokenGenerator,
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
        var session = MerchantUserSession.Issue(
            user.Id, user.MerchantId, user.Username, user.DisplayName, token.Hash, csrfToken, permissions,
            TimeSpan.FromHours(_options.SessionTtlHours), now);

        sessionRepository.Add(session);
        await sessionRepository.SaveChangesAsync(cancellationToken);

        return Result.Success(new MerchantLoginResult(
            token.RawToken, csrfToken, session.ExpiresAt, user.MerchantId, user.Username, user.DisplayName,
            permissions, user.MustChangePassword));
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

        return Result.Success(new MerchantPrincipal(
            session.MerchantUserId, session.MerchantId, session.Username, session.DisplayName, session.PermissionCodes,
            session.CsrfToken));
    }
}
