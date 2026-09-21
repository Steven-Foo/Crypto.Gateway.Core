using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Compliance.Infrastructure.Persistence;

public sealed class ScreeningPolicyRepository(ComplianceDbContext db) : IScreeningPolicyRepository
{
    public Task<ScreeningPolicyVersion?> FindLatestAsync(CancellationToken cancellationToken = default) =>
        db.ScreeningPolicyVersions
            .AsNoTracking()
            // Seq breaks a tie within the same instant, so two versions saved in the same millisecond still
            // have a defined winner rather than an arbitrary one.
            .OrderByDescending(p => p.UpdatedAt)
            .ThenByDescending(p => EF.Property<long>(p, "Seq"))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ScreeningPolicyVersion>> ListAsync(
        int limit, CancellationToken cancellationToken = default) =>
        await db.ScreeningPolicyVersions
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .ThenByDescending(p => EF.Property<long>(p, "Seq"))
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(ScreeningPolicyVersion version, CancellationToken cancellationToken = default)
    {
        db.ScreeningPolicyVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
    }
}
