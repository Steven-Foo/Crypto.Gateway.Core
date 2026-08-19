using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class StaffUserRepository(IdentityDbContext context) : IStaffUserRepository
{
    public Task<StaffUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        context.StaffUsers.SingleOrDefaultAsync(u => u.Username == username, cancellationToken);

    public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default) =>
        context.StaffUsers.AnyAsync(u => u.Username == username, cancellationToken);

    public Task<StaffUser?> GetByIdAsync(Guid staffUserId, CancellationToken cancellationToken = default) =>
        context.StaffUsers.SingleOrDefaultAsync(u => u.Id == staffUserId, cancellationToken);

    public async Task<(IReadOnlyList<StaffUser> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.StaffUsers.AsNoTracking().OrderBy(u => u.Username);

        var total = await context.StaffUsers.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<int> CountActiveAsync(CancellationToken cancellationToken = default) =>
        context.StaffUsers.CountAsync(u => u.Status == StaffUserStatus.Active, cancellationToken);

    public void Add(StaffUser user) => context.StaffUsers.Add(user);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
