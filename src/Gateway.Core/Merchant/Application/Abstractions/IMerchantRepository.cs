using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

public interface IMerchantRepository
{
    Task<Domain.Merchant?> GetByIdAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<Domain.Merchant?> GetByCodeAsync(string merchantCode, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string merchantCode, CancellationToken cancellationToken = default);

    /// <summary>The next sequence number for minting a "ME#####" merchant code — one past the highest
    /// sequence found among existing codes matching that machine-generated shape. Codes that don't match
    /// it (hand-picked dev/legacy codes) are ignored, so they can never perturb the sequence. Starts at 1
    /// when no such code exists yet.</summary>
    Task<int> GetNextMerchantCodeSequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Ordered newest-first, matching APIGateway's BO merchant list. <paramref name="page"/> is 1-based.</summary>
    Task<(IReadOnlyList<Domain.Merchant> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Every IP any OTHER merchant already has allowlisted — lets the caller avoid pushing an
    /// add/remove to Cloudflare for an IP that's still needed by a different merchant sharing it.</summary>
    Task<IReadOnlyList<string>> GetAllAllowedIpsExceptAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a caller's API key to its credential for authentication. Active only.</summary>
    Task<MerchantApiCredential?> FindActiveCredentialAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>The merchant's current (most recently issued) active credential — used to sign outbound callbacks.</summary>
    Task<MerchantApiCredential?> FindActiveCredentialByMerchantAsync(Guid merchantId, CancellationToken cancellationToken = default);

    void Add(Domain.Merchant merchant);

    /// <summary>Saves a merchant just passed to <see cref="Add"/>, returning false instead of throwing if a
    /// concurrent registration already claimed this merchant's generated code — the UNIQUE index on
    /// MerchantCode remains the real arbiter; this is a friendly signal for exactly that one race, not a
    /// general error swallower. On false the merchant is left untracked, so the caller can retry with a
    /// fresh candidate code. Any other persistence failure still throws.</summary>
    Task<bool> TrySaveNewMerchantAsync(Domain.Merchant merchant, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
