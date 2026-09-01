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
        string csrfToken, string? permissionCodesCsv, DateTimeOffset expiresAt, DateTimeOffset now) : base(id)
    {
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

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public static MerchantUserSession Issue(
        Guid merchantUserId, Guid merchantId, string username, string displayName, string tokenHash, string csrfToken,
        IReadOnlyCollection<string> permissionCodes, TimeSpan ttl, DateTimeOffset now)
    {
        var csv = permissionCodes.Count == 0 ? null : string.Join(',', permissionCodes);
        return new MerchantUserSession(
            Guid.CreateVersion7(), merchantUserId, merchantId, username, displayName, tokenHash, csrfToken, csv,
            now.Add(ttl), now);
    }

    /// <summary>Idempotent — revoking an already-revoked session keeps the original revocation time.</summary>
    public Result Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        return Result.Success();
    }
}
