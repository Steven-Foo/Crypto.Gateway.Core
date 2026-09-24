using System.Text;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Identity.Infrastructure.Security;

/// <summary>
/// Key material for TOTP secrets at rest. Identifiers and key bytes only — the same posture as the merchant
/// signing-secret key, and the same seam a KMS-backed source replaces later (§10).
/// </summary>
public sealed class TwoFactorSecretOptions
{
    public const string SectionName = "Identity:TwoFactor:Secrets";

    /// <summary>Base64 AES-256 keys by version. Several may be present so a key can rotate without
    /// re-encrypting: the version travels inside each blob.</summary>
    public Dictionary<int, string> Keys { get; set; } = [];

    public int CurrentKeyVersion { get; set; } = 1;
}

/// <summary>
/// Identity's adapter over <see cref="AesGcmSecretBox"/> — the module's
/// <see cref="ITwoFactorSecretCipher"/> port bound to the keys in <see cref="TwoFactorSecretOptions"/>.
///
/// <para>Deliberately a separate adapter from Merchant's, over the same shared primitive: the two modules
/// must stay independently extractable (§4.5), and they hold different keys for different secrets. What is
/// shared is the algorithm, not the contract and not the key.</para>
///
/// <para>A TOTP secret is raw bytes, so it crosses the string-shaped primitive as Base32 — the same encoding
/// the authenticator was provisioned with, which keeps what is stored and what was scanned obviously the
/// same thing.</para>
/// </summary>
public sealed class AesGcmTwoFactorSecretCipher : ITwoFactorSecretCipher
{
    private readonly Dictionary<int, byte[]> _keys;
    private readonly int _currentVersion;

    public AesGcmTwoFactorSecretCipher(IOptions<TwoFactorSecretOptions> options)
    {
        var value = options.Value;

        // Fail at composition, not at the first enrollment: a host that cannot protect a secret must not
        // start and then discover it when someone is halfway through scanning a QR code.
        if (value.Keys.Count == 0)
            throw new InvalidOperationException($"{TwoFactorSecretOptions.SectionName}: at least one key must be configured.");

        if (!value.Keys.ContainsKey(value.CurrentKeyVersion))
        {
            throw new InvalidOperationException(
                $"{TwoFactorSecretOptions.SectionName}: no key configured for CurrentKeyVersion {value.CurrentKeyVersion}.");
        }

        _keys = value.Keys.ToDictionary(
            k => k.Key,
            k => AesGcmSecretBox.DecodeKey(k.Value, TwoFactorSecretOptions.SectionName));
        _currentVersion = value.CurrentKeyVersion;
    }

    public string Protect(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return AesGcmSecretBox.Protect(Totp.ToBase32(secret), _currentVersion, _keys[_currentVersion]);
    }

    public byte[] Unprotect(string protectedBlob)
    {
        var base32 = AesGcmSecretBox.Unprotect(protectedBlob, version => _keys.GetValueOrDefault(version));

        if (!Totp.TryFromBase32(base32, out var secret))
        {
            // Decryption succeeded (the GCM tag verified) but the contents are not a secret. That means the
            // stored value is corrupt rather than tampered with, and it must surface rather than silently
            // become a factor that can never verify.
            throw new System.Security.Cryptography.CryptographicException(
                "A stored two-factor secret could not be decoded.");
        }

        return secret;
    }
}
