using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class StaffTwoFactorRepository(IdentityDbContext db) : IStaffTwoFactorRepository
{
    public Task<StaffTwoFactor?> FindByStaffUserIdAsync(Guid staffUserId, CancellationToken cancellationToken = default) =>
        db.StaffTwoFactors.FirstOrDefaultAsync(f => f.StaffUserId == staffUserId, cancellationToken);

    public async Task<IReadOnlyCollection<Guid>> ListEnrolledStaffUserIdsAsync(CancellationToken cancellationToken = default) =>
        await db.StaffTwoFactors
            .AsNoTracking()
            .Where(f => f.Status == TwoFactorStatus.Active)
            .Select(f => f.StaffUserId) // ids only — a coverage count must never drag secrets into memory
            .ToListAsync(cancellationToken);

    public void Add(StaffTwoFactor factor) => db.StaffTwoFactors.Add(factor);

    public async Task<IReadOnlyList<StaffRecoveryCode>> ListRecoveryCodesAsync(
        Guid staffUserId, bool unusedOnly, CancellationToken cancellationToken = default)
    {
        var query = db.StaffRecoveryCodes.Where(c => c.StaffUserId == staffUserId);

        if (unusedOnly)
            query = query.Where(c => c.UsedAt == null);

        // Tracked, not AsNoTracking: the caller may consume or delete what comes back.
        return await query.OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);
    }

    public void AddRecoveryCodes(IEnumerable<StaffRecoveryCode> codes) => db.StaffRecoveryCodes.AddRange(codes);

    public void RemoveRecoveryCodes(IEnumerable<StaffRecoveryCode> codes) => db.StaffRecoveryCodes.RemoveRange(codes);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}

public sealed class TwoFactorPolicyRepository(IdentityDbContext db) : ITwoFactorPolicyRepository
{
    /// <summary>
    /// The version in force. Ordered by <c>Seq</c> — the clustered identity, i.e. insertion order — and NOT
    /// by <c>UpdatedAt</c> or <c>Id</c>: two saves inside the same clock tick would tie on the timestamp, and
    /// SQL Server orders a <c>uniqueidentifier</c> by its last six bytes, so <c>Id</c> is deterministic but
    /// unrelated to write order. (The same reasoning the screening directory's "latest" rule records.)
    /// </summary>
    public Task<TwoFactorPolicyVersion?> FindLatestAsync(CancellationToken cancellationToken = default) =>
        db.TwoFactorPolicyVersions
            .AsNoTracking()
            .OrderByDescending(v => EF.Property<long>(v, "Seq"))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<TwoFactorPolicyVersion>> ListHistoryAsync(
        int limit, CancellationToken cancellationToken = default) =>
        await db.TwoFactorPolicyVersions
            .AsNoTracking()
            .OrderByDescending(v => EF.Property<long>(v, "Seq"))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

    public void Add(TwoFactorPolicyVersion version) => db.TwoFactorPolicyVersions.Add(version);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
