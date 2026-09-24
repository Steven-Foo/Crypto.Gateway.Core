using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

/// <summary>
/// Reversible, authenticated protection for a TOTP secret. Reversible because verification needs the secret
/// back — unlike a password, which is only ever checked (<see cref="IStaffPasswordHasher"/>).
///
/// <para>Identity's own port over the shared AES-GCM primitive. It is deliberately not Merchant's
/// <c>ISecretCipher</c>: that interface belongs to another module and referencing it would weld the two
/// together (§4.5). They share the algorithm, not the contract.</para>
/// </summary>
public interface ITwoFactorSecretCipher
{
    string Protect(byte[] secret);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> on a tampered blob
    /// or an unconfigured key version — an unreadable secret must surface, never be treated as "no secret".</summary>
    byte[] Unprotect(string protectedBlob);
}

public interface IStaffTwoFactorRepository
{
    Task<StaffTwoFactor?> FindByStaffUserIdAsync(Guid staffUserId, CancellationToken cancellationToken = default);

    /// <summary>The set of accounts holding an ACTIVE factor, for the enrollment-coverage read. Ids only —
    /// no secrets are loaded.</summary>
    Task<IReadOnlyCollection<Guid>> ListEnrolledStaffUserIdsAsync(CancellationToken cancellationToken = default);

    void Add(StaffTwoFactor factor);

    Task<IReadOnlyList<StaffRecoveryCode>> ListRecoveryCodesAsync(
        Guid staffUserId, bool unusedOnly, CancellationToken cancellationToken = default);

    void AddRecoveryCodes(IEnumerable<StaffRecoveryCode> codes);

    /// <summary>Drops an account's existing codes when a new batch is issued. The old list is printed on
    /// paper somewhere; leaving it live alongside a replacement is how a "regenerate" quietly doubles the
    /// number of valid fallbacks.</summary>
    void RemoveRecoveryCodes(IEnumerable<StaffRecoveryCode> codes);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ITwoFactorPolicyRepository
{
    /// <summary>The version in force, or null when staff have never saved one (so configuration stands).</summary>
    Task<TwoFactorPolicyVersion?> FindLatestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TwoFactorPolicyVersion>> ListHistoryAsync(int limit, CancellationToken cancellationToken = default);

    void Add(TwoFactorPolicyVersion version);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
