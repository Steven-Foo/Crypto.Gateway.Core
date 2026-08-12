using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Infrastructure.Persistence;

public sealed class TreasuryReloadRepository(TreasuryDbContext context) : ITreasuryReloadRepository
{
    public async Task AddAsync(TreasuryReload reload, CancellationToken cancellationToken = default)
    {
        context.Reloads.Add(reload);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<TreasuryReload?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Reloads.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TreasuryReload>> GetByStatusesAsync(
        IReadOnlyCollection<TreasuryReloadStatus> statuses, CancellationToken cancellationToken = default) =>
        await context.Reloads.Where(r => statuses.Contains(r.Status)).ToListAsync(cancellationToken);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
