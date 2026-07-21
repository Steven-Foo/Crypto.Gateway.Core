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

    public void Add(StaffUser user) => context.StaffUsers.Add(user);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
