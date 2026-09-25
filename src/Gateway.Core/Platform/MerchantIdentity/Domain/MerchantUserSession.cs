using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

/// <summary>
/// A merchant-portal session issued at login. Same shape as <c>Platform.Identity.StaffSession</c> — an opaque
/// server-side token, only its <see cref="TokenHash"/> persisted, revocable, fixed-TTL — plus an anti-CSRF
/// token for the httpOnly-cookie browser flow (§ StaffSession.CsrfToken / the Ops cookie+CSRF milestone). The
/// one addition is <see cref="MerchantId"/>: the tenant this session is scoped to, snapshotted at login and the
/// only source of tenant scope for every portal read (a request never supplies its own merchant id).
/// </summary>
public sealed class MerchantUserSession : Entity<Guid>
{
    private MerchantUserSession(
        Guid id, Guid merchantUserId, Guid merchantId, string username, string displayName, string tokenHash,
        string csrfToken, string? permissionCodesCsv, bool requireTwoFactor, DateTimeOffset expiresAt,
        DateTimeOffset now) : base(id)
    {
        RequireTwoFactor = requireTwoFactor;
        MerchantUserId = merchantUserId;
        MerchantId = merchantId;
        Username = username;
        DisplayName = displayName;
        TokenHash = tokenHash;
        CsrfToken = csrfToken;
        PermissionCodesCsv = permissionCodesCsv;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    private MerchantUserSession() : base(Guid.Empty)
    {
    }

    public Guid MerchantUserId { get; private set; }

    /// <summary>The tenant boundary carried on the session — every portal query is scoped to this and nothing
    /// the client supplies.</summary>
    public Guid MerchantId { get; private set; }

    public string Username { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string TokenHash { get; private set; } = null!;

    /// <summary>Per-session anti-CSRF token (plaintext, returned to the browser) — meaningful only alongside the
    /// httpOnly session cookie. See <c>Platform.Identity.StaffSession.CsrfToken</c>.</summary>
    public string CsrfToken { get; private set; } = null!;

    public string? PermissionCodesCsv { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public IReadOnlyList<string> PermissionCodes =>
        string.IsNullOrWhiteSpace(PermissionCodesCsv)
            ? []
            : PermissionCodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Which factor proved this session at login, or null for a session issued before 2FA existed
    /// (or by an account still being forced through enrollment). Stored as its name, so adding a method
    /// later is not a migration.</summary>
    public MerchantTwoFactorMethod? TwoFactorMethod { get; private set; }

    /// <summary>Snapshotted from <see cref="MerchantUser.RequireTwoFactor"/> at login — same principle as
    /// <c>Platform.Identity.StaffSession.RequireTwoFactor</c>: the switch flipping mid-session takes effect on
    /// this account's NEXT login, and false makes both <see cref="TwoFactorEnrolled"/> and
    /// <see cref="AuthenticatorProven"/> read as satisfied unconditionally.</summary>
    public bool RequireTwoFactor { get; private set; }

    /// <summary>False only when the switch is on and this account has not finished enrolling.</summary>
    public bool TwoFactorEnrolled => !RequireTwoFactor || TwoFactorMethod is not null;

    /// <summary>True when an authenticator app proved this session, rather than a printed fallback — or the
    /// switch is off, so there is nothing to prove.</summary>
    public bool AuthenticatorProven => !RequireTwoFactor || TwoFactorMethod == Domain.MerchantTwoFactorMethod.Totp;

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public static MerchantUserSession Issue(
        Guid merchantUserId, Guid merchantId, string username, string displayName, string tokenHash, string csrfToken,
        IReadOnlyCollection<string> permissionCodes, TimeSpan ttl, DateTimeOffset now, bool requireTwoFactor,
        MerchantTwoFactorMethod? twoFactorMethod = null)
    {
        var csv = permissionCodes.Count == 0 ? null : string.Join(',', permissionCodes);
        return new MerchantUserSession(
            Guid.CreateVersion7(), merchantUserId, merchantId, username, displayName, tokenHash, csrfToken, csv,
            requireTwoFactor, now.Add(ttl), now)
        {
            TwoFactorMethod = twoFactorMethod,
        };
    }

    /// <summary>Lifts the enrollment restriction on this session, once its owner has just confirmed
    /// enrollment — so they land in the portal rather than back on a login form.</summary>
    public void MarkAuthenticatorProven() => TwoFactorMethod = Domain.MerchantTwoFactorMethod.Totp;

    /// <summary>Idempotent — revoking an already-revoked session keeps the original revocation time.</summary>
    public Result Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        return Result.Success();
    }
}

/// <summary>How a portal session proved its second factor.</summary>
public enum MerchantTwoFactorMethod
{
    /// <summary>An authenticator app code.</summary>
    Totp = 1,

    /// <summary>A single-use printed fallback. Signs in; nothing more is gated on it in this phase.</summary>
    RecoveryCode = 2,
}
