using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

public interface IMerchantUserRepository
{
    /// <summary>Login's lookup — username is globally unique, so this is the one query NOT scoped by tenant
    /// (it is what resolves which tenant the caller belongs to).</summary>
    Task<MerchantUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Tenant-scoped by design — see <see cref="IMerchantRoleRepository"/>.</summary>
    Task<MerchantUser?> FindByIdAsync(Guid merchantId, Guid merchantUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MerchantUser>> ListAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Active accounts in this tenant — guards "you cannot disable the last one".</summary>
    Task<int> CountActiveAsync(Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>Accounts still pointing at a role — guards deleting a role that is in use.</summary>
    Task<int> CountByRoleAsync(Guid merchantId, Guid roleId, CancellationToken cancellationToken = default);

    void Add(MerchantUser user);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
