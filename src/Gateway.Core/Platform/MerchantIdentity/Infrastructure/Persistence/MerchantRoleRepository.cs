using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

/// <summary>Every query filters on MerchantId as well as the id/name — the tenant boundary is enforced in the
/// WHERE clause, not by the caller checking afterwards.</summary>
public sealed class MerchantRoleRepository(MerchantIdentityDbContext context) : IMerchantRoleRepository
{
    public Task<MerchantRole?> FindByIdAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken = default) =>
        context.MerchantRoles.SingleOrDefaultAsync(r => r.MerchantId == merchantId && r.Id == roleId, cancellationToken);

    public Task<MerchantRole?> FindByNameAsync(Guid merchantId, string name, CancellationToken cancellationToken = default) =>
        context.MerchantRoles.SingleOrDefaultAsync(r => r.MerchantId == merchantId && r.Name == name, cancellationToken);

    public async Task<IReadOnlyList<MerchantRole>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
        await context.MerchantRoles.AsNoTracking()
            .Where(r => r.MerchantId == merchantId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public void Add(MerchantRole role) => context.MerchantRoles.Add(role);

    public void Remove(MerchantRole role) => context.MerchantRoles.Remove(role);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
