using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// AES-256-GCM at-rest protection for a secret the server must be able to <em>recover</em> — a merchant's
/// request-signing secret, a staff member's TOTP secret. Real encryption, not obfuscation: a fresh 96-bit
/// nonce per call makes ciphertexts non-deterministic, and the GCM authentication tag turns any tampering
/// into a decrypt failure rather than a silent wrong value.
///
/// <para>Blob layout — <c>base64( version[4] ‖ nonce[12] ‖ tag[16] ‖ ciphertext )</c>. The key version
/// travels with the data, so keys rotate without re-encryption or a schema change. This format is unchanged
/// from the Merchant cipher this was extracted from, so every blob already in the database still decrypts.</para>
///
/// <para>A cross-cutting <b>primitive</b>, not business logic (§4.8): it takes key bytes and returns bytes,
/// and knows nothing about merchants, staff, options sections or KMS. Callers keep their own port over it
/// (<c>Merchant.ISecretCipher</c>, <c>Identity.ITwoFactorSecretCipher</c>) and own where the key comes from —
/// configuration today, KMS later, at the same seam (§10). Consolidated here rather than copied per module
/// for the reason <see cref="AmountConversion"/> was: one security primitive is one place to review, and
/// three copies are three things that can drift.</para>
/// </summary>
public static class AesGcmSecretBox
{
    private const int VersionSize = 4;
    private const int NonceSize = 12; // 96-bit GCM nonce (standard)
    private const int TagSize = 16;   // 128-bit GCM tag (maximum)

    /// <summary>AES-256. A key of any other length is a configuration error, not something to pad or truncate.</summary>
    public const int KeySize = 32;

    public static string Protect(string plaintext, int keyVersion, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        RequireKeySize(key);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[VersionSize + NonceSize + TagSize + plainBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(blob, keyVersion);

        var nonce = blob.AsSpan(VersionSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var tag = blob.AsSpan(VersionSize + NonceSize, TagSize);
        var cipher = blob.AsSpan(VersionSize + NonceSize + TagSize);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a blob produced by <see cref="Protect"/>. <paramref name="keyForVersion"/> resolves the key
    /// the blob names; returning null means that version is not configured, which is a
    /// <see cref="CryptographicException"/> and never a silent empty string.
    /// </summary>
    public static string Unprotect(string protectedBlob, Func<int, byte[]?> keyForVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedBlob);
        ArgumentNullException.ThrowIfNull(keyForVersion);

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
        var key = keyForVersion(version)
            ?? throw new CryptographicException($"No key configured for protected-secret version {version}.");

        RequireKeySize(key);

        var nonce = blob.AsSpan(VersionSize, NonceSize);
        var tag = blob.AsSpan(VersionSize + NonceSize, TagSize);
        var cipher = blob.AsSpan(VersionSize + NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        // Throws AuthenticationTagMismatchException (a CryptographicException) if tampered with.
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Decodes a base64 key from configuration, refusing anything that is not exactly AES-256.
    /// <paramref name="context"/> names the settings section, so a misconfiguration says where to fix it.</summary>
    public static byte[] DecodeKey(string? configured, string context)
    {
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{context}: key must not be empty.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{context}: key must be base64-encoded.");
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException($"{context}: key must be {KeySize} bytes (AES-256).");

        return key;
    }

    private static void RequireKeySize(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new CryptographicException($"Key must be {KeySize} bytes (AES-256).");
    }
}
