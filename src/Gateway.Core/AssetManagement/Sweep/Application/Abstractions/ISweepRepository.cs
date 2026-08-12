using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;

public interface ISweepRepository
{
    /// <summary>All sweeps in any of the given statuses — the workers' working set.</summary>
    Task<IReadOnlyList<Domain.Sweep>> GetByStatusesAsync(
        IReadOnlyCollection<SweepStatus> statuses, CancellationToken cancellationToken = default);

    /// <summary>True if a non-terminal (Pending/Signing/Broadcast) sweep already exists for this wallet and
    /// asset — the scanner's belt to avoid creating a duplicate. The filtered unique index is the real arbiter.</summary>
    Task<bool> HasInFlightAsync(Guid walletId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new sweep, saving immediately. Returns false when the one-in-flight-per-(wallet, asset)
    /// unique index rejects it — a concurrent scan already created one — so the caller drops it rather than
    /// double-sweeping. Keeps the EF-specific race translation inside Infrastructure (§4.4).
    /// </summary>
    Task<bool> TryAddAsync(Domain.Sweep sweep, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
