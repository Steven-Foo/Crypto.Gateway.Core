using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

public interface IDefaultFeePolicyRepository
{
    Task<IReadOnlyList<DefaultFeePolicy>> ListAsync(CancellationToken cancellationToken = default);

    Task<DefaultFeePolicy?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken = default);

    void Add(DefaultFeePolicy policy);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
