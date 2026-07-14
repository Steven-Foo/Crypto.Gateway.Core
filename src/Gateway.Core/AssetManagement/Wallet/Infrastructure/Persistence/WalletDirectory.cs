using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;

using WalletEntity = Domain.Wallet;

/// <summary>Read-only projection for other modules. Never returns the aggregate itself.</summary>
public sealed class WalletDirectory(WalletDbContext context) : IWalletDirectory
{
    public Task<WalletOwnership?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
        Project(context.Wallets.AsNoTracking().Where(w => w.Chain == chain && w.Address == address))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<WalletOwnership?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
        Project(context.Wallets.AsNoTracking().Where(w => w.Id == walletId))
            .SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<WalletOwnership> Project(IQueryable<WalletEntity> query) =>
        query.Select(w => new WalletOwnership(
            w.Id,
            w.DerivedKeyId,
            w.Chain,
            w.Address,
            w.WalletType.ToString(),
            w.MerchantId,
            w.Status == WalletStatus.Active));
}
