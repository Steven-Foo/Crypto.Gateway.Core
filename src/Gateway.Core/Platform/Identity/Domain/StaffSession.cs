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
        Guid id, Guid staffUserId, string tokenHash, StaffRole role, DateTimeOffset expiresAt, DateTimeOffset now) : base(id)
    {
        StaffUserId = staffUserId;
        TokenHash = tokenHash;
        Role = role;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    private StaffSession() : base(Guid.Empty)
    {
    }

    public Guid StaffUserId { get; private set; }
    public string TokenHash { get; private set; } = null!;

    /// <summary>
    /// Snapshotted from the staff user at login, not re-read per request — a role change (were that ever
    /// built) takes effect on next login, not mid-session. Avoids a second lookup on every authenticated call.
    /// </summary>
    public StaffRole Role { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    public static StaffSession Issue(Guid staffUserId, string tokenHash, StaffRole role, TimeSpan ttl, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), staffUserId, tokenHash, role, now.Add(ttl), now);

    /// <summary>Idempotent — revoking an already-revoked session keeps the original revocation time.</summary>
    public Result Revoke(DateTimeOffset now)
    {
        RevokedAt ??= now;
        return Result.Success();
    }
}
