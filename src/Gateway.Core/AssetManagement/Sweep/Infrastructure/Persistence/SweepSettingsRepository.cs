using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Infrastructure.Persistence;

public sealed class SweepSettingsRepository(SweepDbContext context) : ISweepSettingsRepository
{
    public Task<SweepSettings?> FindAsync(Chain chain, CancellationToken cancellationToken = default) =>
        context.SweepSettings.SingleOrDefaultAsync(s => s.Chain == chain, cancellationToken);

    public async Task<IReadOnlyList<SweepSettings>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.SweepSettings.ToListAsync(cancellationToken);

    public async Task<SweepSettings> AddOrGetAsync(
        SweepSettings settings, CancellationToken cancellationToken = default)
    {
        context.SweepSettings.Add(settings);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Another instance created the chain's row first (UX_SweepSettings_Chain). Adopt theirs rather
            // than failing: the row is a schedule, and two hosts starting together is the normal case.
            context.Entry(settings).State = EntityState.Detached;
            return await context.SweepSettings.SingleAsync(s => s.Chain == settings.Chain, cancellationToken);
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);
}
