using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

public interface IStaffSessionRepository
{
    Task<StaffSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    void Add(StaffSession session);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
