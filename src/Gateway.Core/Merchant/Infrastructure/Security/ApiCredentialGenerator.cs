using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// All values come from a CSPRNG. The bearer secret is 256 bits — high enough entropy that a fast HMAC
/// is a sound choice for hashing it (see <see cref="HmacApiSecretHasher"/>). The signing secret is a
/// 32-byte key rendered as 64 hex chars, because the partner's signing scheme hex-decodes it to the raw
/// HMAC key — matching their existing merchants and SDKs exactly.
/// </summary>
public sealed class ApiCredentialGenerator : IApiCredentialGenerator
{
    private const string KeyPrefix = "cpe_";
    private const int KeyEntropyBytes = 16;
    private const int SecretEntropyBytes = 32;
    private const int SigningSecretBytes = 32;

    public GeneratedApiCredential Generate() =>
        new(KeyPrefix + Base64Url(KeyEntropyBytes), Base64Url(SecretEntropyBytes), Hex(SigningSecretBytes));

    /// <summary>URL-safe, unpadded — safe in headers, query strings, and copy-paste.</summary>
    private static string Base64Url(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>Lower-case hex, so the partner's <c>hexDecode(secret)</c> yields the raw HMAC key.</summary>
    private static string Hex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
}
