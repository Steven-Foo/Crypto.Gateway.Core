using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// Merchant's adapter over <see cref="AesGcmSecretBox"/> — this module's <see cref="ISecretCipher"/> port
/// bound to the keys in <see cref="SigningSecretOptions"/>.
///
/// <para>The algorithm itself moved to the SharedKernel primitive when the identity modules needed the same
/// at-rest protection for TOTP secrets and could not reference this module (§4.5). The blob format is
/// unchanged — <c>base64( version[4] ‖ nonce[12] ‖ tag[16] ‖ ciphertext )</c> — so every secret already
/// stored still decrypts.</para>
///
/// <para>What stays here is the part that is Merchant's: which keys exist, which version is current, and the
/// configuration section they are read from. Key bytes come from configuration today and a KMS-backed source
/// later, at this same seam, with no re-encryption or schema change (§10).</para>
/// </summary>
public sealed class AesGcmSecretCipher : ISecretCipher
{
    private readonly Dictionary<int, byte[]> _keys;
    private readonly int _currentVersion;

    public AesGcmSecretCipher(IOptions<SigningSecretOptions> options)
    {
        var value = options.Value;

        if (value.Keys.Count == 0)
            throw new InvalidOperationException($"{SigningSecretOptions.SectionName}: at least one key must be configured.");

        if (!value.Keys.ContainsKey(value.CurrentKeyVersion))
        {
            throw new InvalidOperationException(
                $"{SigningSecretOptions.SectionName}: no key configured for CurrentKeyVersion {value.CurrentKeyVersion}.");
        }

        _keys = value.Keys.ToDictionary(
            k => k.Key,
            k => AesGcmSecretBox.DecodeKey(k.Value, SigningSecretOptions.SectionName));
        _currentVersion = value.CurrentKeyVersion;
    }

    public string Protect(string plaintext) =>
        AesGcmSecretBox.Protect(plaintext, _currentVersion, _keys[_currentVersion]);

    public string Unprotect(string protectedBlob) =>
        AesGcmSecretBox.Unprotect(protectedBlob, version => _keys.GetValueOrDefault(version));
}
