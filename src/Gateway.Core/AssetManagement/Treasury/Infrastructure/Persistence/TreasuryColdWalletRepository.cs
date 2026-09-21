using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;

public sealed class TreasuryColdWalletRepository(TreasuryDbContext context) : ITreasuryColdWalletRepository
{
    public Task<TreasuryColdWallet?> FindActiveAsync(
        Chain chain, ColdWalletKind kind, CancellationToken cancellationToken = default) =>
        context.ColdWallets.SingleOrDefaultAsync(
            w => w.Chain == chain && w.Kind == kind && w.Status == ColdWalletStatus.Active, cancellationToken);

    public Task<TreasuryColdWallet?> FindByIdAsync(Guid walletId, CancellationToken cancellationToken = default) =>
        context.ColdWallets.SingleOrDefaultAsync(w => w.Id == walletId, cancellationToken);

    public Task<TreasuryColdWallet?> FindByAddressAsync(
        Chain chain, string address, CancellationToken cancellationToken = default) =>
        context.ColdWallets.SingleOrDefaultAsync(w => w.Chain == chain && w.Address == address, cancellationToken);

    public async Task<IReadOnlyList<TreasuryColdWallet>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.ColdWallets.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TreasuryColdWallet>> ListForChainAsync(
        Chain chain, CancellationToken cancellationToken = default) =>
        await context.ColdWallets.AsNoTracking().Where(w => w.Chain == chain).ToListAsync(cancellationToken);

    /// <summary>Stages the row. The caller saves — activating a wallet retires another in the same
    /// transaction, and a repository that committed on its own would split that in two.</summary>
    public async Task AddAsync(TreasuryColdWallet wallet, CancellationToken cancellationToken = default)
    {
        await context.ColdWallets.AddAsync(wallet, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Runs several saves as one unit. Needed because designating a destination is two statements that the
    /// filtered unique index will not tolerate being interleaved or half-applied: the previous wallet must
    /// be retired before the new one becomes active, and a crash between the two would leave the chain with
    /// no destination at all.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        // A caller may already be inside one (the seeder path); joining it keeps this composable.
        if (context.Database.CurrentTransaction is not null)
            return await action(cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var result = await action(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
