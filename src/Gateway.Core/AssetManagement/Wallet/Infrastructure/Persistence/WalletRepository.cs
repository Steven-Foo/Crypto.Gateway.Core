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

    public async Task<bool> TryAddAsync(WalletEntity wallet, CancellationToken cancellationToken = default)
    {
        context.Wallets.Add(wallet);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // The unique (Chain, Address) index rejected it — a concurrent registration for the same
            // address won. Detach so the context stays reusable, then let the caller adopt the winner.
            context.Entry(wallet).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<(IReadOnlyList<WalletAdminRow> Items, int TotalCount)> SearchAsync(
        WalletAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.Wallets.AsNoTracking()
            .Where(w => filter.MerchantId == null || w.MerchantId == filter.MerchantId)
            .Where(w => filter.Address == null || w.Address == filter.Address)
            .Where(w => filter.Chain == null || w.Chain == filter.Chain)
            .Where(w => filter.Status == null || w.Status == filter.Status);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new WalletAdminRow(
                w.Id, w.MerchantId, w.Chain, w.Address, w.WalletType.ToString(), w.Status.ToString(),
                w.StatusReason, w.DepositsReceivedCount, w.CreatedAt, w.UpdatedAt))
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
