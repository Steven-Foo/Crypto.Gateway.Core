using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>Reads a merchant's cash-out liquidity cap from its asset policy. Missing policy ⇒ no cap.</summary>
public sealed class MerchantWithdrawalCapReader(MerchantDbContext context) : IMerchantWithdrawalCap
{
    public async Task<MerchantWithdrawalCap> GetAsync(
        Guid merchantId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var policy = await context.AssetPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.MerchantId == merchantId && p.AssetId == assetId, cancellationToken);

        return policy is null
            ? MerchantWithdrawalCap.None
            : new MerchantWithdrawalCap(policy.MerchantWithdrawalFlatCap, policy.MerchantWithdrawalPercentBps);
    }
}
