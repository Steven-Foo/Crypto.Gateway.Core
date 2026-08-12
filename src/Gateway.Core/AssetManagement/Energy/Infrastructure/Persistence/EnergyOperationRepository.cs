using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure.Persistence;

public sealed class EnergyOperationRepository(EnergyDbContext context) : IEnergyOperationRepository
{
    private static readonly EnergyOperationStatus[] InFlight =
        [EnergyOperationStatus.Pending, EnergyOperationStatus.Signing, EnergyOperationStatus.Broadcast];

    public async Task<IReadOnlyList<EnergyOperation>> GetByStatusesAsync(
        IReadOnlyCollection<EnergyOperationStatus> statuses, CancellationToken cancellationToken = default) =>
        // Tracked: the processing/confirmation services mutate these and SaveChanges.
        await context.EnergyOperations.Where(o => statuses.Contains(o.Status)).ToListAsync(cancellationToken);

    public Task<bool> HasInFlightStakeAsync(Guid stakingWalletId, CancellationToken cancellationToken = default) =>
        context.EnergyOperations.AsNoTracking().AnyAsync(
            o => o.Kind == EnergyOperationKind.Stake && o.StakingWalletId == stakingWalletId && InFlight.Contains(o.Status),
            cancellationToken);

    public Task<bool> HasInFlightDelegateAsync(Chain chain, string targetAddress, CancellationToken cancellationToken = default) =>
        context.EnergyOperations.AsNoTracking().AnyAsync(
            o => o.Kind == EnergyOperationKind.Delegate && o.Chain == chain && o.TargetAddress == targetAddress && InFlight.Contains(o.Status),
            cancellationToken);

    public async Task<bool> TryAddAsync(EnergyOperation operation, CancellationToken cancellationToken = default)
    {
        context.EnergyOperations.Add(operation);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // A one-in-flight unique index rejected it — a concurrent stake/delegate already exists. Detach so
            // the context stays reusable, then let the caller drop this operation.
            context.Entry(operation).State = EntityState.Detached;
            return false;
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
