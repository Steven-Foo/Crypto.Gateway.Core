using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

public interface IStaffUserRepository
{
    Task<StaffUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default);

    Task<StaffUser?> GetByIdAsync(Guid staffUserId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<StaffUser> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Count of accounts currently <see cref="StaffUserStatus.Active"/> — the last-active-account
    /// guard's source of truth (§ IStaffAccountService.SetStatusAsync).</summary>
    Task<int> CountActiveAsync(CancellationToken cancellationToken = default);

    void Add(StaffUser user);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
