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
        var limits = await context.AssetPolicies.AsNoTracking()
            .Where(p => p.MerchantId == merchantId && p.AssetId == assetId)
            .Select(p => new { p.MinimumDeposit, p.MaximumDeposit })
            .SingleOrDefaultAsync(cancellationToken);

        return limits is null
            ? MerchantDepositLimits.None
            : new MerchantDepositLimits(limits.MinimumDeposit, limits.MaximumDeposit);
    }
}
