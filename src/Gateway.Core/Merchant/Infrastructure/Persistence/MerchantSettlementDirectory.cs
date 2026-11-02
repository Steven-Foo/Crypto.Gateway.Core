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

    public async Task<ApprovedSettlementWallet?> FindApprovedSettlementWalletAsync(
        Guid merchantId, Chain chain, Guid walletId, CancellationToken cancellationToken = default) =>
        // Scoped by merchant AND chain in the query itself, so a foreign or wrong-chain id simply isn't found.
        await context.SettlementWallets.AsNoTracking()
            .Where(w => w.Id == walletId && w.MerchantId == merchantId && w.Chain == chain
                        && (w.Status == SettlementWalletStatus.Active || w.Status == SettlementWalletStatus.Retired))
            .Select(w => new ApprovedSettlementWallet(
                w.Id, w.Address, w.Status == SettlementWalletStatus.Active, w.ScreeningDecision))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SettlementWalletRef>> ListAllAsync(
        CancellationToken cancellationToken = default) =>
        await context.SettlementWallets.AsNoTracking()
            // Every APPROVED wallet — active and standby. The re-screen exists to catch an address that can still
            // be paid going bad, and a merchant may choose an approved standby for a cash-out. Pending, rejected
            // and cancelled ones can be paid nothing, so screening them would spend quota for nothing.
            .Where(w => w.Status == SettlementWalletStatus.Active || w.Status == SettlementWalletStatus.Retired)
            // Ordered so a re-screen pass that is cut short by a restart resumes over the same sequence
            // rather than a different arbitrary one, which would let the same wallets be favoured forever.
            .OrderBy(w => w.MerchantId).ThenBy(w => w.Chain)
            .Select(w => new SettlementWalletRef(w.MerchantId, w.Chain, w.Address))
            .ToListAsync(cancellationToken);
}
