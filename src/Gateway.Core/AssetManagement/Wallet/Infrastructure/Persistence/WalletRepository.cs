using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure.Persistence;

using WalletEntity = Domain.Wallet;

public sealed class WalletRepository(WalletDbContext context) : IWalletRepository
{
    public Task<WalletEntity?> GetByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
        context.Wallets
            .Include(w => w.Assignments)
            .SingleOrDefaultAsync(w => w.Id == walletId, cancellationToken);

    public Task<WalletEntity?> GetByDerivedKeyIdAsync(Guid derivedKeyId, CancellationToken cancellationToken = default) =>
        context.Wallets
            .Include(w => w.Assignments)
            .SingleOrDefaultAsync(w => w.DerivedKeyId == derivedKeyId, cancellationToken);

    public Task<WalletEntity?> FindByAddressAsync(Chain chain, string address, CancellationToken cancellationToken = default) =>
        context.Wallets
            .Include(w => w.Assignments)
            .SingleOrDefaultAsync(w => w.Chain == chain && w.Address == address, cancellationToken);

    public void Add(WalletEntity wallet) => context.Wallets.Add(wallet);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
