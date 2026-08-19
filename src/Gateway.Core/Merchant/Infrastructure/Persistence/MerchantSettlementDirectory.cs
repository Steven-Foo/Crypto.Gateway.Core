using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>Reads the merchant's whitelisted settlement address for a chain — the cash-out destination seam.</summary>
public sealed class MerchantSettlementDirectory(MerchantDbContext context) : IMerchantSettlementDirectory
{
    public async Task<string?> FindSettlementAddressAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
        await context.SettlementWallets.AsNoTracking()
            .Where(w => w.MerchantId == merchantId && w.Chain == chain)
            .Select(w => w.Address)
            .SingleOrDefaultAsync(cancellationToken);
}
