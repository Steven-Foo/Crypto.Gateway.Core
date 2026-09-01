using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantUserRepository(MerchantIdentityDbContext context) : IMerchantUserRepository
{
    public Task<MerchantUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        context.MerchantUsers.SingleOrDefaultAsync(u => u.Username == username, cancellationToken);

    public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default) =>
        context.MerchantUsers.AnyAsync(u => u.Username == username, cancellationToken);

    // Tenant-filtered in the WHERE clause: another tenant's account id simply yields null.
    public Task<MerchantUser?> FindByIdAsync(Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default) =>
        context.MerchantUsers.SingleOrDefaultAsync(
            u => u.MerchantId == merchantId && u.Id == merchantUserId, cancellationToken);

    public async Task<IReadOnlyList<MerchantUser>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
        await context.MerchantUsers.AsNoTracking()
            .Where(u => u.MerchantId == merchantId)
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken);

    public Task<int> CountActiveAsync(Guid merchantId, CancellationToken cancellationToken = default) =>
        context.MerchantUsers.CountAsync(
            u => u.MerchantId == merchantId && u.Status == MerchantUserStatus.Active, cancellationToken);

    public Task<int> CountByRoleAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken = default) =>
        context.MerchantUsers.CountAsync(u => u.MerchantId == merchantId && u.RoleId == roleId, cancellationToken);

    public void Add(MerchantUser user) => context.MerchantUsers.Add(user);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
