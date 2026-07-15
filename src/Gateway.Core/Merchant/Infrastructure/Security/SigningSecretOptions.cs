namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Master keys for encrypting merchant signing secrets at rest, keyed by version. These are secrets:
/// they belong in a KMS/secret store and must never be written to the database, source, or logs (§10).
/// Keeping old versions here is what lets a stored secret keep decrypting after a key rotation.
/// </summary>
public sealed class SigningSecretOptions
{
    public const string SectionName = "Merchant:SigningSecrets";

    public int CurrentKeyVersion { get; init; } = 1;

    /// <summary>version -> 32-byte AES-256 key, base64-encoded. Must contain <see cref="CurrentKeyVersion"/>.</summary>
    public IReadOnlyDictionary<int, string> Keys { get; init; } = new Dictionary<int, string>();
}
