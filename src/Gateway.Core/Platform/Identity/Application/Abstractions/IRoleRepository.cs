using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Role> Items, int TotalCount)> GetPagedAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Whether any <see cref="StaffUser"/> currently references this role — the delete precheck
    /// (a friendly error before the FK ever fires, same pattern as <c>MerchantErrors.CodeAlreadyExists</c>).</summary>
    Task<bool> IsAssignedToAnyStaffUserAsync(Guid roleId, CancellationToken cancellationToken = default);

    void Add(Role role);

    void Remove(Role role);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
