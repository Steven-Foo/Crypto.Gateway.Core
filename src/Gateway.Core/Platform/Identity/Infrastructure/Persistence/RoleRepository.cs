using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class RoleRepository(IdentityDbContext context) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        context.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);

    public Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
        context.Roles.SingleOrDefaultAsync(r => r.Name == name, cancellationToken);

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default) =>
        context.Roles.AnyAsync(r => r.Name == name, cancellationToken);

    public async Task<(IReadOnlyList<Role> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.Roles.AsNoTracking().OrderBy(r => r.Name);

        var total = await context.Roles.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return (items, total);
    }

    public Task<bool> IsAssignedToAnyStaffUserAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        context.StaffUsers.AnyAsync(u => u.RoleId == roleId, cancellationToken);

    public void Add(Role role) => context.Roles.Add(role);

    public void Remove(Role role) => context.Roles.Remove(role);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
