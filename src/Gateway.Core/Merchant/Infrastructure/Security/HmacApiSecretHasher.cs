using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// HMAC-SHA256 with a server-side pepper.
///
/// A slow KDF (Argon2/PBKDF2) is deliberately NOT used: those exist to defend low-entropy human
/// passwords against offline brute force. API secrets here are 256 bits of CSPRNG output, so a
/// single HMAC is already infeasible to brute force, and a slow KDF would only add latency to every
/// authenticated request. The pepper (not in the DB) is what protects against an offline attack on
/// a stolen credential table.
/// </summary>
public sealed class HmacApiSecretHasher : IApiSecretHasher
{
    private readonly Dictionary<int, byte[]> _peppers;

    public HmacApiSecretHasher(IOptions<ApiCredentialOptions> options)
    {
        var value = options.Value;

        if (value.Peppers.Count == 0)
            throw new InvalidOperationException($"{ApiCredentialOptions.SectionName}: at least one pepper must be configured.");

        if (!value.Peppers.ContainsKey(value.CurrentHashVersion))
        {
            throw new InvalidOperationException(
                $"{ApiCredentialOptions.SectionName}: no pepper configured for CurrentHashVersion {value.CurrentHashVersion}.");
        }

        _peppers = value.Peppers.ToDictionary(p => p.Key, p => Decode(p.Value));
        CurrentVersion = value.CurrentHashVersion;
    }

    public int CurrentVersion { get; }

    public string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return Convert.ToBase64String(Compute(secret, _peppers[CurrentVersion]));
    }

    public bool Verify(string secret, string secretHash, int version)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(secretHash))
            return false;

        if (!_peppers.TryGetValue(version, out var pepper))
            return false;

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        if (!Convert.TryFromBase64String(secretHash, expected, out var written) || written != expected.Length)
            return false;

        var actual = Compute(secret, pepper);

        // Constant-time: a short-circuiting comparison leaks how many leading bytes matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string secret, byte[] pepper) =>
        HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(secret));

    /// <summary>Accepts a base64 pepper; falls back to raw UTF-8 so local dev config stays readable.</summary>
    private static byte[] Decode(string pepper)
    {
        if (string.IsNullOrWhiteSpace(pepper))
            throw new InvalidOperationException($"{ApiCredentialOptions.SectionName}: pepper must not be empty.");

        Span<byte> buffer = stackalloc byte[pepper.Length];
        return Convert.TryFromBase64String(pepper, buffer, out var written)
            ? buffer[..written].ToArray()
            : Encoding.UTF8.GetBytes(pepper);
    }
}
