using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

/// <summary>
/// Every lookup is scoped by <c>merchantId</c> on purpose: a role is owned by one tenant, so "get role by id"
/// alone would be a cross-tenant read waiting to happen. Passing the caller's tenant makes the isolation a
/// property of the query, not of the caller remembering to check afterwards.
/// </summary>
public interface IMerchantRoleRepository
{
    Task<MerchantRole?> FindByIdAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken = default);

    Task<MerchantRole?> FindByNameAsync(Guid merchantId, string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MerchantRole>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default);

    void Add(MerchantRole role);

    void Remove(MerchantRole role);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
