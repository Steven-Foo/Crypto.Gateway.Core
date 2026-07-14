using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Both values come from a CSPRNG. The secret is 256 bits — high enough entropy that a fast HMAC
/// is a sound choice for hashing it (see <see cref="HmacApiSecretHasher"/>).
/// </summary>
public sealed class ApiCredentialGenerator : IApiCredentialGenerator
{
    private const string KeyPrefix = "cpe_";
    private const int KeyEntropyBytes = 16;
    private const int SecretEntropyBytes = 32;

    public GeneratedApiCredential Generate() =>
        new(KeyPrefix + Base64Url(KeyEntropyBytes), Base64Url(SecretEntropyBytes));

    /// <summary>URL-safe, unpadded — safe in headers, query strings, and copy-paste.</summary>
    private static string Base64Url(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
