using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;

public interface ITreasuryReloadRepository
{
    Task AddAsync(TreasuryReload reload, CancellationToken cancellationToken = default);

    Task<TreasuryReload?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Reloads in any of the given statuses — the worker's working set (tracked for mutation).</summary>
    Task<IReadOnlyList<TreasuryReload>> GetByStatusesAsync(
        IReadOnlyCollection<TreasuryReloadStatus> statuses, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
