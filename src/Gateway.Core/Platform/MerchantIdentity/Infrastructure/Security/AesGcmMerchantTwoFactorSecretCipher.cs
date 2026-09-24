using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Infrastructure.Security;

/// <summary>
/// Key material for portal TOTP secrets. Its OWN section, holding its OWN keys — deliberately not the staff
/// module's: the two identity modules must stay independently extractable (§4.5), and a single compromised
/// key should not unlock both platform staff and every merchant's users.
/// </summary>
public sealed class MerchantTwoFactorSecretOptions
{
    public const string SectionName = "MerchantIdentity:TwoFactor:Secrets";

    /// <summary>Base64 AES-256 keys by version. Several may be present so a key can rotate without
    /// re-encrypting: the version travels inside each blob.</summary>
    public Dictionary<int, string> Keys { get; set; } = [];

    public int CurrentKeyVersion { get; set; } = 1;
}

/// <summary>This module's adapter over <see cref="AesGcmSecretBox"/>. The secret crosses the string-shaped
/// primitive as Base32 — the same encoding the authenticator was provisioned with.</summary>
public sealed class AesGcmMerchantTwoFactorSecretCipher : IMerchantTwoFactorSecretCipher
{
    private readonly Dictionary<int, byte[]> _keys;
    private readonly int _currentVersion;

    public AesGcmMerchantTwoFactorSecretCipher(IOptions<MerchantTwoFactorSecretOptions> options)
    {
        var value = options.Value;

        // Fail at composition: a host that cannot protect a secret must not start and then discover it when
        // someone is halfway through scanning a QR code.
        if (value.Keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"{MerchantTwoFactorSecretOptions.SectionName}: at least one key must be configured.");
        }

        if (!value.Keys.ContainsKey(value.CurrentKeyVersion))
        {
            throw new InvalidOperationException(
                $"{MerchantTwoFactorSecretOptions.SectionName}: no key configured for CurrentKeyVersion {value.CurrentKeyVersion}.");
        }

        _keys = value.Keys.ToDictionary(
            k => k.Key,
            k => AesGcmSecretBox.DecodeKey(k.Value, MerchantTwoFactorSecretOptions.SectionName));
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
            // The GCM tag verified, so this is corruption rather than tampering — it must surface rather
            // than silently become a factor that can never verify.
            throw new System.Security.Cryptography.CryptographicException(
                "A stored two-factor secret could not be decoded.");
        }

        return secret;
    }
}
