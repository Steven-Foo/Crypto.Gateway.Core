using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

public interface IMerchantRepository
{
    Task<Domain.Merchant?> GetByIdAsync(Guid merchantId, CancellationToken cancellationToken = default);

    Task<Domain.Merchant?> GetByCodeAsync(string merchantCode, CancellationToken cancellationToken = default);

    Task<bool> CodeExistsAsync(string merchantCode, CancellationToken cancellationToken = default);

    /// <summary>Resolves a caller's API key to its credential for authentication. Active only.</summary>
    Task<MerchantApiCredential?> FindActiveCredentialAsync(string apiKey, CancellationToken cancellationToken = default);

    void Add(Domain.Merchant merchant);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
