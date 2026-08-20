using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>Reads a merchant's per-asset approval-threshold override. A missing policy — or a set policy whose
/// threshold is null — both yield null (the withdrawal flow uses the platform config threshold).</summary>
public sealed class MerchantApprovalThresholdReader(MerchantDbContext context) : IMerchantApprovalThreshold
{
    public async Task<BigInteger?> GetAsync(
        Guid merchantId, Guid assetId, CancellationToken cancellationToken = default) =>
        await context.AssetPolicies.AsNoTracking()
            .Where(p => p.MerchantId == merchantId && p.AssetId == assetId)
            .Select(p => p.ApprovalThreshold)
            .SingleOrDefaultAsync(cancellationToken);
}
