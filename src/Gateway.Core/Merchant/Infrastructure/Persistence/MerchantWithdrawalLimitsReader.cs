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
        var own = await context.AssetPolicies.AsNoTracking()
            .Where(p => p.MerchantId == merchantId && p.AssetId == assetId)
            .Select(p => new { p.MinimumWithdrawal, p.MaximumWithdrawal })
            .SingleOrDefaultAsync(cancellationToken);
        var platform = await context.DefaultFeePolicies.AsNoTracking()
            .Where(p => p.AssetId == assetId)
            .Select(p => new { p.MinimumWithdrawal, p.MaximumWithdrawal })
            .SingleOrDefaultAsync(cancellationToken);

        // Per bound: the merchant's own value, else the platform default; still null ⇒ the caller's config.
        return new MerchantWithdrawalLimits(
            own?.MinimumWithdrawal ?? platform?.MinimumWithdrawal,
            own?.MaximumWithdrawal ?? platform?.MaximumWithdrawal);
    }
}
