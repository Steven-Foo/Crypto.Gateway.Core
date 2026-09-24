using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

/// <summary>This module's own cipher port over the shared AES-GCM primitive. Deliberately not Identity's or
/// Merchant's: the modules stay independently extractable (§4.5) and hold different keys.</summary>
public interface IMerchantTwoFactorSecretCipher
{
    string Protect(byte[] secret);

    byte[] Unprotect(string protectedBlob);
}

public interface IMerchantTwoFactorRepository
{
    /// <summary>Tenant-scoped lookup. A merchant passing another tenant's user id gets null, so a foreign id
    /// reads as "not found" rather than being actionable.</summary>
    Task<MerchantUserTwoFactor?> FindAsync(
        Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lookup by user alone, for the LOGIN path only — at that point there is no session to supply a tenant,
    /// and the user id came from a username lookup the caller has already authenticated with a password.
    /// Everything reachable from a session uses <see cref="FindAsync"/>.
    /// </summary>
    Task<MerchantUserTwoFactor?> FindByUserAsync(Guid merchantUserId, CancellationToken cancellationToken = default);

    void Add(MerchantUserTwoFactor factor);

    Task<IReadOnlyList<MerchantUserRecoveryCode>> ListRecoveryCodesAsync(
        Guid merchantUserId, bool unusedOnly, CancellationToken cancellationToken = default);

    void AddRecoveryCodes(IEnumerable<MerchantUserRecoveryCode> codes);

    void RemoveRecoveryCodes(IEnumerable<MerchantUserRecoveryCode> codes);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
