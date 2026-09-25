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
        string? permissionCodesCsv, bool requireTwoFactor, DateTimeOffset expiresAt, DateTimeOffset now) : base(id)
    {
        RequireTwoFactor = requireTwoFactor;
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

    /// <summary>
    /// Which second factor proved this session at login, or null for a session issued before 2FA existed (or
    /// by an account still being forced through enrollment).
    ///
    /// <para>It is recorded because a guarded action must be able to refuse a session that only ever proved
    /// itself with a recovery code (§ <see cref="StaffRecoveryCode"/>) — a printed fallback signs you in, it
    /// does not authorise moving money. It is also what the audit trail attributes an action to.</para>
    /// </summary>
    public TwoFactorMethod? TwoFactorMethod { get; private set; }

    /// <summary>Snapshotted from <see cref="StaffUser.RequireTwoFactor"/> at login — same principle as
    /// <see cref="RoleName"/>/<see cref="PermissionCodesCsv"/>: the switch flipping mid-session takes effect
    /// on this account's NEXT login, not retroactively on a session already issued. False makes both
    /// <see cref="TwoFactorEnrolled"/> and <see cref="AuthenticatorProven"/> read as satisfied unconditionally,
    /// which is what lets a switch-off session skip 2FA everywhere without touching <see cref="TwoFactorMethod"/>
    /// at all (it stays an honest record of what was actually proved, or null if nothing was).</summary>
    public bool RequireTwoFactor { get; private set; }

    /// <summary>False only when the switch is on and this account has not finished enrolling — the one case
    /// the host middleware restricts to the enrollment routes.</summary>
    public bool TwoFactorEnrolled => !RequireTwoFactor || TwoFactorMethod is not null;

    /// <summary>True when this session may perform a guarded action: either the switch is off for this
    /// account (nothing to prove), or an authenticator app — not a recovery code — proved it at login.</summary>
    public bool AuthenticatorProven => !RequireTwoFactor || TwoFactorMethod == Domain.TwoFactorMethod.Totp;

    public IReadOnlyList<string> PermissionCodes =>
        string.IsNullOrWhiteSpace(PermissionCodesCsv)
            ? []
            : PermissionCodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public static StaffSession Issue(
        Guid staffUserId, string username, string tokenHash, string csrfToken, Guid roleId, string roleName,
        IReadOnlyCollection<string> permissionCodes, TimeSpan ttl, DateTimeOffset now, bool requireTwoFactor,
        TwoFactorMethod? twoFactorMethod = null)
    {
        var csv = permissionCodes.Count == 0 ? null : string.Join(',', permissionCodes);
        return new StaffSession(
            Guid.CreateVersion7(), staffUserId, username, tokenHash, csrfToken, roleId, roleName, csv,
            requireTwoFactor, now.Add(ttl), now)
        {
            TwoFactorMethod = twoFactorMethod,
        };
    }

    /// <summary>
    /// Records that this session has now completed enrollment, upgrading it in place.
    ///
    /// <para>The alternative — forcing a re-login after scanning the QR — is worse than it sounds: it lands
    /// a brand-new user back on a login form immediately after setup, to type a code from an app they have
    /// only just added, which is exactly where people conclude the setup failed.</para>
    /// </summary>
    public void MarkAuthenticatorProven() => TwoFactorMethod = Domain.TwoFactorMethod.Totp;

    /// <summary>Idempotent — revoking an already-revoked session keeps the original revocation time.</summary>
    public Result Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        return Result.Success();
    }
}

/// <summary>How a session proved its second factor. Stored as a string, so adding a method later (WebAuthn,
/// say) is not a migration.</summary>
public enum TwoFactorMethod
{
    /// <summary>An authenticator app code. The only method a guarded action accepts.</summary>
    Totp = 1,

    /// <summary>A single-use printed fallback. Good enough to sign in, never enough to move money.</summary>
    RecoveryCode = 2,
}
