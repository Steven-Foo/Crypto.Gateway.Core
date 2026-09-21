using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;

public interface ISweepSettingsRepository
{
    Task<SweepSettings?> FindAsync(Chain chain, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SweepSettings>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts the row, or returns the one a concurrent caller inserted first. The unique index on Chain is
    /// the arbiter — two hosts booting together must not end up with two schedules for one chain.
    /// </summary>
    Task<SweepSettings> AddOrGetAsync(SweepSettings settings, CancellationToken cancellationToken = default);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
