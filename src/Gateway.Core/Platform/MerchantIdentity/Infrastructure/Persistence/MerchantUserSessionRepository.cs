using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Persistence;

public sealed class MerchantUserSessionRepository(MerchantIdentityDbContext context) : IMerchantUserSessionRepository
{
    public Task<MerchantUserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        context.MerchantUserSessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public void Add(MerchantUserSession session) => context.MerchantUserSessions.Add(session);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
}
