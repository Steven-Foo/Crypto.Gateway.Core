using System.Buffers.Binary;
using System.Security.Cryptography;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// The at-rest protection for merchant signing secrets. It must genuinely encrypt (no plaintext, fresh
/// nonce per call), detect tampering (GCM tag), and survive key rotation — this is real crypto, not a
/// dev placeholder.
/// </summary>
public sealed class SecretCipherTests
{
    private static string Key(byte seed = 7) => Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray());

    private static AesGcmSecretCipher Cipher(int current = 1, params (int Version, string Key)[] keys)
    {
        var configured = keys.Length > 0 ? keys : [(1, Key())];
        return new AesGcmSecretCipher(Options.Create(new SigningSecretOptions
        {
            CurrentKeyVersion = current,
            Keys = configured.ToDictionary(k => k.Version, k => k.Key),
        }));
    }

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var cipher = Cipher();
        const string secret = "0123456789abcdef0123456789abcdef";
        cipher.Unprotect(cipher.Protect(secret)).ShouldBe(secret);
    }

    [Fact]
    public void The_blob_never_contains_the_plaintext()
    {
        const string secret = "super-secret-signing-key";
        Cipher().Protect(secret).ShouldNotContain(secret);
    }

    [Fact]
    public void Each_protect_uses_a_fresh_nonce_so_ciphertexts_differ()
    {
        var cipher = Cipher();
        cipher.Protect("same").ShouldNotBe(cipher.Protect("same"));
    }

    [Fact]
    public void A_tampered_blob_is_rejected_not_silently_decrypted()
    {
        var cipher = Cipher();
        var blob = Convert.FromBase64String(cipher.Protect("secret"));
        blob[^1] ^= 0xFF; // flip a bit in the ciphertext/tag region

        Should.Throw<CryptographicException>(() => cipher.Unprotect(Convert.ToBase64String(blob)));
    }

    [Fact]
    public void A_blob_encrypted_under_an_old_key_still_decrypts_after_rotation()
    {
        var blob = Cipher(1, (1, Key(1))).Protect("legacy-secret");

        var rotated = Cipher(2, (1, Key(1)), (2, Key(2)));
        rotated.Unprotect(blob).ShouldBe("legacy-secret");

        // New writes use the current key version, carried in the blob header.
        var fresh = Convert.FromBase64String(rotated.Protect("x"));
        BinaryPrimitives.ReadInt32BigEndian(fresh).ShouldBe(2);
    }

    [Fact]
    public void Unprotect_rejects_a_blob_whose_key_version_is_not_configured()
    {
        var v2Blob = Cipher(2, (2, Key(2))).Protect("secret");
        Should.Throw<CryptographicException>(() => Cipher(1, (1, Key(1))).Unprotect(v2Blob));
    }

    [Fact]
    public void Construction_requires_a_key_for_the_current_version() =>
        Should.Throw<InvalidOperationException>(() => Cipher(2, (1, Key())));

    [Fact]
    public void Construction_rejects_a_key_that_is_not_32_bytes() =>
        Should.Throw<InvalidOperationException>(() => Cipher(1, (1, Convert.ToBase64String(new byte[16]))));
}
