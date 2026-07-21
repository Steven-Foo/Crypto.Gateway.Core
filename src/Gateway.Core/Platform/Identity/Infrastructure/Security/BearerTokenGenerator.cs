using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>
/// The token itself is 256 bits of CSPRNG output — high entropy, machine-generated, same reasoning as
/// Merchant's bearer secret: a fast unkeyed hash (SHA-256) is sufficient here, unlike passwords.
/// </summary>
public sealed class BearerTokenGenerator : IBearerTokenGenerator
{
    private const int TokenBytes = 32;

    public GeneratedBearerToken Generate()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return new GeneratedBearerToken(raw, HashOf(raw));
    }

    public string HashOf(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
