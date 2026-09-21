using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>Reads the merchant's whitelisted settlement address for a chain — the cash-out destination seam.</summary>
public sealed class MerchantSettlementDirectory(MerchantDbContext context) : IMerchantSettlementDirectory
{
    public async Task<string?> FindSettlementAddressAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default) =>
        // The ACTIVE one only. A merchant may have several addresses on file per chain; a retired one is a
        // record of a past approval, not somewhere to send money.
        await context.SettlementWallets.AsNoTracking()
            .Where(w => w.MerchantId == merchantId && w.Chain == chain && w.Status == SettlementWalletStatus.Active)
            .Select(w => w.Address)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SettlementWalletRef>> ListAllAsync(
        CancellationToken cancellationToken = default) =>
        await context.SettlementWallets.AsNoTracking()
            // Active only: the re-screen exists to catch an address that is still being paid to going bad.
            // A retired one is paid nothing, and screening it would spend quota to learn nothing actionable.
            .Where(w => w.Status == SettlementWalletStatus.Active)
            // Ordered so a re-screen pass that is cut short by a restart resumes over the same sequence
            // rather than a different arbitrary one, which would let the same wallets be favoured forever.
            .OrderBy(w => w.MerchantId).ThenBy(w => w.Chain)
            .Select(w => new SettlementWalletRef(w.MerchantId, w.Chain, w.Address))
            .ToListAsync(cancellationToken);
}
