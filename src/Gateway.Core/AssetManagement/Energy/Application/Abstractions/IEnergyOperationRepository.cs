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

    /// <summary>True if a non-terminal TopUp (native-TRX transfer) already targets this address on this chain.</summary>
    Task<bool> HasInFlightTopUpAsync(Chain chain, string targetAddress, CancellationToken cancellationToken = default);

    /// <summary>True if a non-terminal operation of <paramref name="kind"/> exists for this staking wallet.</summary>
    Task<bool> HasInFlightAsync(Guid stakingWalletId, EnergyOperationKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Addresses the staking wallet has lent energy to that are due a take-back: the latest confirmed Delegate
    /// to the address finished at or before <paramref name="confirmedBefore"/>, no take-back has been made since
    /// it, and no Delegate to it is in progress. Oldest first, at most <paramref name="max"/>.
    /// <paramref name="excludedTargets"/> are never returned (the platform's own wallets, which keep their energy).
    /// </summary>
    Task<IReadOnlyList<string>> ListReclaimCandidatesAsync(
        Chain chain, Guid stakingWalletId, DateTimeOffset confirmedBefore, IReadOnlyCollection<string> excludedTargets,
        int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new operation, saving immediately. Returns false when a one-in-flight unique index rejects it
    /// (a concurrent stake/delegate already exists) — the caller then drops it. Keeps the EF-specific race
    /// translation inside Infrastructure (§4.4).
    /// </summary>
    Task<bool> TryAddAsync(EnergyOperation operation, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
