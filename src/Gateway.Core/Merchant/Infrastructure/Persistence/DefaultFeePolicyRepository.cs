using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

public sealed class DefaultFeePolicyRepository(MerchantDbContext context) : IDefaultFeePolicyRepository
{
    public async Task<IReadOnlyList<DefaultFeePolicy>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.DefaultFeePolicies.AsNoTracking().ToListAsync(cancellationToken);

    public Task<DefaultFeePolicy?> FindByAssetIdAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        context.DefaultFeePolicies.SingleOrDefaultAsync(p => p.AssetId == assetId, cancellationToken);

    public void Add(DefaultFeePolicy policy) => context.DefaultFeePolicies.Add(policy);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
