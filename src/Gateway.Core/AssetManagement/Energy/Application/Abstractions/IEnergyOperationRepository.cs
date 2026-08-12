using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;

public interface IEnergyOperationRepository
{
    /// <summary>All operations in any of the given statuses — the workers' working set.</summary>
    Task<IReadOnlyList<EnergyOperation>> GetByStatusesAsync(
        IReadOnlyCollection<EnergyOperationStatus> statuses, CancellationToken cancellationToken = default);

    /// <summary>True if a non-terminal Stake operation already exists for this staking wallet.</summary>
    Task<bool> HasInFlightStakeAsync(Guid stakingWalletId, CancellationToken cancellationToken = default);

    /// <summary>True if a non-terminal Delegate operation already targets this address on this chain.</summary>
    Task<bool> HasInFlightDelegateAsync(Chain chain, string targetAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new operation, saving immediately. Returns false when a one-in-flight unique index rejects it
    /// (a concurrent stake/delegate already exists) — the caller then drops it. Keeps the EF-specific race
    /// translation inside Infrastructure (§4.4).
    /// </summary>
    Task<bool> TryAddAsync(EnergyOperation operation, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
