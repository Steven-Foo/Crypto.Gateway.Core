using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;

using WalletEntity = Domain.Wallet;

/// <summary>Read-only projection of the active platform (non-merchant) wallets, for the Energy monitor.</summary>
public sealed class PlatformWalletDirectory(WalletDbContext context) : IPlatformWalletDirectory
{
    public async Task<IReadOnlyList<PlatformWallet>> GetPlatformWalletsAsync(
        Chain chain, CancellationToken cancellationToken = default) =>
        await context.Wallets.AsNoTracking()
            .Where(w => w.Chain == chain
                        && w.Status == WalletStatus.Active
                        && w.WalletType != WalletType.Deposit)
            .Select(w => new PlatformWallet(w.Id, w.Chain, w.Address, w.WalletType.ToString()))
            .ToListAsync(cancellationToken);
}
