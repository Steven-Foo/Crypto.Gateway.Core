using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;

public interface IMerchantTwoFactorPolicyRepository
{
    /// <summary>The platform-minimum version in force (<c>MerchantId</c> null), or null when staff have never
    /// saved one.</summary>
    Task<MerchantTwoFactorPolicyVersion?> FindLatestPlatformAsync(CancellationToken cancellationToken = default);

    /// <summary>One merchant's own-additions version in force, or null when that merchant has never saved one.
    /// Tenant-scoped: it can only ever return that merchant's rows.</summary>
    Task<MerchantTwoFactorPolicyVersion?> FindLatestForMerchantAsync(
        Guid merchantId, CancellationToken cancellationToken = default);

    /// <summary>History of one layer, newest first: <paramref name="merchantId"/> null ⇒ the platform minimum,
    /// otherwise that merchant's additions only.</summary>
    Task<IReadOnlyList<MerchantTwoFactorPolicyVersion>> ListHistoryAsync(
        Guid? merchantId, int limit, CancellationToken cancellationToken = default);

    void Add(MerchantTwoFactorPolicyVersion version);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
