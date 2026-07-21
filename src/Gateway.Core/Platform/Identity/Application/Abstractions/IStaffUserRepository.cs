using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

public interface IStaffUserRepository
{
    Task<StaffUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default);

    void Add(StaffUser user);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
