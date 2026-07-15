using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;

/// <summary>
/// AES-256-GCM at-rest protection for merchant signing secrets. This is <b>real</b> encryption, not a
/// dev placeholder: a fresh 96-bit nonce per call makes ciphertexts non-deterministic, and the GCM
/// authentication tag makes any tampering a decrypt failure rather than a silent wrong value.
///
/// The blob is <c>base64( version[4] ‖ nonce[12] ‖ tag[16] ‖ ciphertext )</c>. The version travels with
/// the data so a rotated key still decrypts old blobs. Key bytes come from <see cref="SigningSecretOptions"/>
/// today and a KMS-backed source later — the same seam, with no re-encryption or schema change (§10).
/// </summary>
public sealed class AesGcmSecretCipher : ISecretCipher
{
    private const int VersionSize = 4;
    private const int NonceSize = 12; // 96-bit GCM nonce (standard)
    private const int TagSize = 16;   // 128-bit GCM tag (max)
    private const int KeySize = 32;   // AES-256

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

        _keys = value.Keys.ToDictionary(k => k.Key, k => DecodeKey(k.Value));
        _currentVersion = value.CurrentKeyVersion;
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var key = _keys[_currentVersion];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        var blob = new byte[VersionSize + NonceSize + TagSize + plainBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(blob, _currentVersion);

        var nonce = blob.AsSpan(VersionSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var tag = blob.AsSpan(VersionSize + NonceSize, TagSize);
        var cipher = blob.AsSpan(VersionSize + NonceSize + TagSize);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string protectedBlob)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedBlob);

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedBlob);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Protected secret is not valid base64.", ex);
        }

        if (blob.Length < VersionSize + NonceSize + TagSize)
            throw new CryptographicException("Protected secret is malformed.");

        var version = BinaryPrimitives.ReadInt32BigEndian(blob);
        if (!_keys.TryGetValue(version, out var key))
            throw new CryptographicException($"No signing-secret key configured for version {version}.");

        var nonce = blob.AsSpan(VersionSize, NonceSize);
        var tag = blob.AsSpan(VersionSize + NonceSize, TagSize);
        var cipher = blob.AsSpan(VersionSize + NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        // Throws AuthenticationTagMismatchException (a CryptographicException) if tampered.
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DecodeKey(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{SigningSecretOptions.SectionName}: key must not be empty.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{SigningSecretOptions.SectionName}: key must be base64-encoded.");
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException($"{SigningSecretOptions.SectionName}: key must be {KeySize} bytes (AES-256).");

        return key;
    }
}
