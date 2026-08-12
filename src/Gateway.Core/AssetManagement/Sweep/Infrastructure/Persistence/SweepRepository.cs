using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

public sealed class SweepRepository(SweepDbContext context) : ISweepRepository
{
    private static readonly SweepStatus[] InFlight = [SweepStatus.Pending, SweepStatus.Signing, SweepStatus.Broadcast];

    public async Task<IReadOnlyList<Domain.Sweep>> GetByStatusesAsync(
        IReadOnlyCollection<SweepStatus> statuses, CancellationToken cancellationToken = default) =>
        // Tracked (not AsNoTracking): the processing/confirmation services mutate these and SaveChanges.
        await context.Sweeps.Where(s => statuses.Contains(s.Status)).ToListAsync(cancellationToken);

    public Task<bool> HasInFlightAsync(Guid walletId, Guid assetId, CancellationToken cancellationToken = default) =>
        context.Sweeps.AsNoTracking()
            .AnyAsync(s => s.WalletId == walletId && s.AssetId == assetId && InFlight.Contains(s.Status), cancellationToken);

    public async Task<bool> TryAddAsync(Domain.Sweep sweep, CancellationToken cancellationToken = default)
    {
        context.Sweeps.Add(sweep);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // The one-in-flight-per-(wallet, asset) unique index rejected it — a concurrent scan won the race.
            // Detach so the context stays reusable, then let the caller drop this sweep.
            context.Entry(sweep).State = EntityState.Detached;
            return false;
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
