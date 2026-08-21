using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

/// <summary>
/// A bearer session issued at login. Only the <see cref="TokenHash"/> is ever persisted — the raw token is
/// returned to the caller once, at issue time, and never stored (same principle as
/// <c>MerchantApiCredential.SecretHash</c>). Logout is a real revoke, not a wait-for-expiry: <see cref="Revoke"/>
/// sets <see cref="RevokedAt"/>, and <see cref="IsValid"/> checks both that and the TTL.
/// </summary>
public sealed class StaffSession : Entity<Guid>
{
    private StaffSession(
        Guid id, Guid staffUserId, string username, string tokenHash, string csrfToken, Guid roleId, string roleName,
        string? permissionCodesCsv, DateTimeOffset expiresAt, DateTimeOffset now) : base(id)
    {
        StaffUserId = staffUserId;
        Username = username;
        TokenHash = tokenHash;
        CsrfToken = csrfToken;
        RoleId = roleId;
        RoleName = roleName;
        PermissionCodesCsv = permissionCodesCsv;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    private StaffSession() : base(Guid.Empty)
    {
    }

    public Guid StaffUserId { get; private set; }

    /// <summary>Snapshotted at login, same principle as <see cref="RoleName"/> — lets a caller (e.g. the
    /// audit log) attribute an action to a username without a second lookup, and without depending on
    /// Identity's Domain/Application (§4.5): the host passes this through as plain data.</summary>
    public string Username { get; private set; } = null!;

    public string TokenHash { get; private set; } = null!;

    /// <summary>
    /// The session's anti-CSRF token — a per-session random value, set at issue. Unlike <see cref="TokenHash"/>
    /// this is stored in plaintext and IS returned to the browser (in the login + /auth/me body, so the SPA can
    /// echo it as an <c>X-CSRF-Token</c> header). That is safe: the CSRF token grants nothing on its own — a
    /// request is only authenticated by the httpOnly session cookie, whose value is never in the DB. Binding the
    /// CSRF token to the session means it rotates on every login and dies on logout/expiry. Only meaningful for
    /// cookie-authenticated requests; a bearer-header caller is inherently CSRF-safe (§ StaffBearerAuthMiddleware).
    /// </summary>
    public string CsrfToken { get; private set; } = null!;

    /// <summary>
    /// The role's id/name/permission-codes are snapshotted from the <see cref="Role"/> at login, not
    /// re-read per request — a role's permissions changing takes effect on next login, not mid-session
    /// (avoids a second lookup on every authenticated call, same principle the old enum-based Role had).
    /// </summary>
    public Guid RoleId { get; private set; }

    public string RoleName { get; private set; } = null!;
    public string? PermissionCodesCsv { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public IReadOnlyList<string> PermissionCodes =>
        string.IsNullOrWhiteSpace(PermissionCodesCsv)
            ? []
            : PermissionCodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public static StaffSession Issue(
        Guid staffUserId, string username, string tokenHash, string csrfToken, Guid roleId, string roleName,
        IReadOnlyCollection<string> permissionCodes, TimeSpan ttl, DateTimeOffset now)
    {
        var csv = permissionCodes.Count == 0 ? null : string.Join(',', permissionCodes);
        return new StaffSession(
            Guid.CreateVersion7(), staffUserId, username, tokenHash, csrfToken, roleId, roleName, csv, now.Add(ttl), now);
    }

    /// <summary>Idempotent — revoking an already-revoked session keeps the original revocation time.</summary>
    public Result Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        return Result.Success();
    }
}
