using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>Reads a merchant's per-asset deposit (payin) min/max override. A missing policy ⇒
/// <see cref="MerchantDepositLimits.None"/> (the invoice flow uses the platform default).</summary>
public sealed class MerchantDepositLimitsReader(MerchantDbContext context) : IMerchantDepositLimits
{
    public async Task<MerchantDepositLimits> GetAsync(
        Guid merchantId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var own = await context.AssetPolicies.AsNoTracking()
            .Where(p => p.MerchantId == merchantId && p.AssetId == assetId)
            .Select(p => new { p.MinimumDeposit, p.MaximumDeposit })
            .SingleOrDefaultAsync(cancellationToken);
        var platform = await context.DefaultFeePolicies.AsNoTracking()
            .Where(p => p.AssetId == assetId)
            .Select(p => new { p.MinimumDeposit, p.MaximumDeposit })
            .SingleOrDefaultAsync(cancellationToken);

        // Per bound: the merchant's own value, else the platform default; still null ⇒ the caller's config.
        return new MerchantDepositLimits(
            own?.MinimumDeposit ?? platform?.MinimumDeposit,
            own?.MaximumDeposit ?? platform?.MaximumDeposit);
    }
}
