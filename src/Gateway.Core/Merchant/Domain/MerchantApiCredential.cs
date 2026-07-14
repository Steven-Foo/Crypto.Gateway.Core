using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// A merchant's API credential. <see cref="ApiKey"/> is a public identifier stored in clear;
/// the secret is NEVER stored — only <see cref="SecretHash"/>, produced by the
/// <c>IApiSecretHasher</c> port. The raw secret is shown to the merchant exactly once, at issue.
///
/// <see cref="HashVersion"/> records which server pepper produced the hash. HMAC hashes cannot be
/// re-derived without the raw secret, so without this column rotating the pepper would permanently
/// lock out every merchant.
/// </summary>
public sealed class MerchantApiCredential : Entity<Guid>
{
    private MerchantApiCredential(
        Guid id,
        Guid merchantId,
        string apiKey,
        string secretHash,
        int hashVersion,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        ApiKey = apiKey;
        SecretHash = secretHash;
        HashVersion = hashVersion;
        Status = CredentialStatus.Active;
        CreatedAt = createdAt;
    }

    private MerchantApiCredential() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public string ApiKey { get; private set; } = null!;
    public string SecretHash { get; private set; } = null!;
    public int HashVersion { get; private set; }
    public CredentialStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => Status == CredentialStatus.Active;

    internal static MerchantApiCredential Issue(
        Guid merchantId,
        string apiKey,
        string secretHash,
        int hashVersion,
        DateTimeOffset createdAt) =>
        new(Guid.CreateVersion7(), merchantId, apiKey, secretHash, hashVersion, createdAt);

    internal Result Revoke(DateTimeOffset revokedAt)
    {
        if (Status == CredentialStatus.Revoked)
            return Result.Failure(MerchantErrors.CredentialAlreadyRevoked);

        Status = CredentialStatus.Revoked;
        RevokedAt = revokedAt;
        return Result.Success();
    }
}
