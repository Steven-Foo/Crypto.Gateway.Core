using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;

public sealed class TreasuryColdWalletRepository(TreasuryDbContext context) : ITreasuryColdWalletRepository
{
    public Task<TreasuryColdWallet?> FindByChainAsync(Chain chain, CancellationToken cancellationToken = default) =>
        context.ColdWallets.SingleOrDefaultAsync(w => w.Chain == chain, cancellationToken);

    public async Task AddAsync(TreasuryColdWallet wallet, CancellationToken cancellationToken = default)
    {
        context.ColdWallets.Add(wallet);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
