namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

/// <summary>
/// Reversible, authenticated protection for a secret the server must be able to <em>recover</em> — namely
/// a merchant's request-signing secret, which we have to hold to recompute an inbound HMAC signature and
/// to sign outbound callbacks. This is the symmetric counterpart to <see cref="IApiSecretHasher"/> (which
/// is one-way, for bearer secrets that are never recovered).
///
/// The returned blob is self-describing — it carries the version of the key that produced it — so keys can
/// rotate without a schema change or re-encryption. The key bytes come from configuration today; a
/// KMS-backed source swaps in later by DI, behind this same port (§10). The plaintext must never be logged.
/// </summary>
public interface ISecretCipher
{
    /// <summary>Encrypts <paramref name="plaintext"/>, returning a versioned, tamper-evident base64 blob.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a blob produced by <see cref="Protect"/>. Throws <see cref="System.Security.Cryptography.CryptographicException"/>
    /// if the blob was tampered with or its key version is not configured — a decrypt failure is never a silent empty string.
    /// </summary>
    string Unprotect(string protectedBlob);
}
