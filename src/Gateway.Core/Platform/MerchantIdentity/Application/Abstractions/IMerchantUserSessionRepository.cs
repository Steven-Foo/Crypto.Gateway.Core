using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

public interface IMerchantUserSessionRepository
{
    Task<MerchantUserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    void Add(MerchantUserSession session);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
