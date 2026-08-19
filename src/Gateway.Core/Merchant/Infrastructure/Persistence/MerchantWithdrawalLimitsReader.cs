using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>Reads a merchant's per-asset user-withdrawal min/max override. A missing policy ⇒
/// <see cref="MerchantWithdrawalLimits.None"/> (the flow uses the platform config limits).</summary>
public sealed class MerchantWithdrawalLimitsReader(MerchantDbContext context) : IMerchantWithdrawalLimits
{
    public async Task<MerchantWithdrawalLimits> GetAsync(
        Guid merchantId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var limits = await context.AssetPolicies.AsNoTracking()
            .Where(p => p.MerchantId == merchantId && p.AssetId == assetId)
            .Select(p => new { p.MinimumWithdrawal, p.MaximumWithdrawal })
            .SingleOrDefaultAsync(cancellationToken);

        return limits is null
            ? MerchantWithdrawalLimits.None
            : new MerchantWithdrawalLimits(limits.MinimumWithdrawal, limits.MaximumWithdrawal);
    }
}
