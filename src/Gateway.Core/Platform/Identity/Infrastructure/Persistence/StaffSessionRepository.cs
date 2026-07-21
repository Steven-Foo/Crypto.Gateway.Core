using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Persistence;

public sealed class StaffSessionRepository(IdentityDbContext context) : IStaffSessionRepository
{
    public Task<StaffSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        context.StaffSessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public void Add(StaffSession session) => context.StaffSessions.Add(session);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
