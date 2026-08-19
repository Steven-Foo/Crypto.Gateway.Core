using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>18 bytes from a CSPRNG, URL-safe base64 — ~24 characters, high entropy, and copy-pasteable
/// without escaping issues in a terminal/JSON response (same encoding convention as
/// <c>ApiCredentialGenerator</c>'s bearer secret).</summary>
public sealed class StaffPasswordGenerator : IStaffPasswordGenerator
{
    private const int EntropyBytes = 18;

    public string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(EntropyBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
