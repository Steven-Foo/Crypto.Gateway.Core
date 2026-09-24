using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.SharedKernel.Tests;

/// <summary>
/// The load-bearing proof for <see cref="Totp"/> is <see cref="Rfc6238_published_test_vectors"/>: it checks
/// the implementation against the vectors printed in RFC 6238 Appendix B rather than against itself. An
/// algorithm test that only asserts self-consistency passes just as happily when the algorithm is wrong,
/// which for an authentication factor is the whole risk.
/// </summary>
public class TotpTests
{
    /// <summary>The RFC's SHA-1 seed: the ASCII string "12345678901234567890" (20 bytes).</summary>
    private static byte[] RfcSeed => Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    // RFC 6238 Appendix B, the SHA-1 rows. Values are 8-digit, as the RFC prints them.
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Rfc6238_published_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Totp.ComputeAt(RfcSeed, step, digits: 8).ShouldBe(expected);
    }

    [Fact]
    public void Step_advances_once_per_period()
    {
        var at0 = DateTimeOffset.FromUnixTimeSeconds(0);

        Totp.StepAt(at0).ShouldBe(0);
        Totp.StepAt(at0.AddSeconds(29)).ShouldBe(0);
        Totp.StepAt(at0.AddSeconds(30)).ShouldBe(1);
        Totp.StepAt(at0.AddSeconds(59)).ShouldBe(1);
    }

    [Fact]
    public void Verify_accepts_the_current_code()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.ComputeAt(secret, Totp.StepAt(now));

        Totp.Verify(secret, code, now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1, true)]  // the phone is one step behind
    [InlineData(0, true)]
    [InlineData(1, true)]   // the phone is one step ahead
    [InlineData(-2, false)] // beyond the accepted skew
    [InlineData(2, false)]
    public void Verify_accepts_exactly_one_step_of_skew(int stepOffset, bool expected)
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.ComputeAt(secret, Totp.StepAt(now) + stepOffset);

        Totp.Verify(secret, code, now).ShouldBe(expected);
    }

    /// <summary>
    /// The platform deliberately does NOT consume codes (docs/two-factor-authentication.md §6.1): an operator
    /// working through a queue must not be paced at one action per 30-second step. This pins that behaviour
    /// so it cannot be "tightened" by accident without the decision being revisited.
    /// </summary>
    [Fact]
    public void Verify_does_not_consume_the_code()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.ComputeAt(secret, Totp.StepAt(now));

        Totp.Verify(secret, code, now).ShouldBeTrue();
        Totp.Verify(secret, code, now).ShouldBeTrue();
        Totp.Verify(secret, code, now).ShouldBeTrue();
    }

    [Fact]
    public void Verify_rejects_a_code_from_a_different_secret()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.ComputeAt(Totp.GenerateSecret(), Totp.StepAt(now));

        Totp.Verify(Totp.GenerateSecret(), code, now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]    // too short
    [InlineData("1234567")]  // too long
    [InlineData("12a456")]   // not digits
    [InlineData("12 34 5")]  // still the wrong length once grouping is stripped
    public void Verify_rejects_malformed_input_without_throwing(string? code)
    {
        Totp.Verify(Totp.GenerateSecret(), code, DateTimeOffset.UtcNow).ShouldBeFalse();
    }

    /// <summary>Authenticators display codes grouped and people paste them that way.</summary>
    [Fact]
    public void Verify_tolerates_grouping_whitespace_and_dashes()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.ComputeAt(secret, Totp.StepAt(now));

        Totp.Verify(secret, $"{code[..3]} {code[3..]}", now).ShouldBeTrue();
        Totp.Verify(secret, $"{code[..3]}-{code[3..]}", now).ShouldBeTrue();
    }

    [Fact]
    public void Generated_secrets_are_random_and_long_enough()
    {
        var a = Totp.GenerateSecret();
        var b = Totp.GenerateSecret();

        a.Length.ShouldBe(20); // 160 bits, the RFC 4226 reference length
        a.ShouldNotBe(b);
    }

    [Theory]
    // RFC 4648 §10 test vectors.
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc4648(string input, string expected)
    {
        Totp.ToBase32(Encoding.ASCII.GetBytes(input)).ShouldBe(expected);
    }

    [Fact]
    public void Base32_round_trips_a_secret()
    {
        var secret = Totp.GenerateSecret();

        Totp.TryFromBase32(Totp.ToBase32(secret), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(secret);
    }

    [Fact]
    public void Base32_rejects_characters_outside_the_alphabet()
    {
        // '1', '8' and '0' are excluded from the alphabet precisely because they are misread; dropping them
        // silently would decode to a different secret than the one displayed.
        Totp.TryFromBase32("MZXW6YTB1", out _).ShouldBeFalse();
        Totp.TryFromBase32("!!!!", out _).ShouldBeFalse();
    }

    [Fact]
    public void Provisioning_uri_carries_everything_an_authenticator_needs()
    {
        var secret = Totp.GenerateSecret();

        var uri = Totp.ProvisioningUri("CryptoPaymentEngine", "admin@example.com", secret);

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain($"secret={Totp.ToBase32(secret)}");
        uri.ShouldContain("issuer=CryptoPaymentEngine"); // repeated as a parameter, not only in the label
        uri.ShouldContain("digits=6");
        uri.ShouldContain("period=30");
        uri.ShouldContain("algorithm=SHA1");
        // The label is escaped, so an account name with a ':' or '/' cannot break the URI.
        Totp.ProvisioningUri("CPE", "a/b:c", secret).ShouldContain("a%2Fb%3Ac");
    }
}

public class AesGcmSecretBoxTests
{
    private static byte[] Key(byte seed) => Enumerable.Repeat(seed, AesGcmSecretBox.KeySize).ToArray();

    [Fact]
    public void Round_trips_a_secret()
    {
        var key = Key(7);

        var blob = AesGcmSecretBox.Protect("s3cret-value", 1, key);

        AesGcmSecretBox.Unprotect(blob, _ => key).ShouldBe("s3cret-value");
    }

    [Fact]
    public void Ciphertext_is_non_deterministic()
    {
        var key = Key(7);

        // Same plaintext, same key, different blobs — a fresh nonce each time. Deterministic ciphertext would
        // leak that two accounts share a secret.
        AesGcmSecretBox.Protect("same", 1, key).ShouldNotBe(AesGcmSecretBox.Protect("same", 1, key));
    }

    [Fact]
    public void Tampering_is_a_failure_not_a_wrong_value()
    {
        var key = Key(7);
        var blob = Convert.FromBase64String(AesGcmSecretBox.Protect("s3cret-value", 1, key));
        blob[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(
            () => AesGcmSecretBox.Unprotect(Convert.ToBase64String(blob), _ => key));
    }

    [Fact]
    public void Decrypting_with_the_wrong_key_fails()
    {
        var blob = AesGcmSecretBox.Protect("s3cret-value", 1, Key(7));

        Should.Throw<CryptographicException>(() => AesGcmSecretBox.Unprotect(blob, _ => Key(9)));
    }

    [Fact]
    public void An_unknown_key_version_fails_loudly()
    {
        var blob = AesGcmSecretBox.Protect("s3cret-value", 2, Key(7));

        // Never a silent empty string: an unreadable secret must surface, not be treated as "no secret".
        Should.Throw<CryptographicException>(() => AesGcmSecretBox.Unprotect(blob, _ => null));
    }

    [Fact]
    public void Key_version_travels_with_the_blob_so_keys_can_rotate()
    {
        var oldKey = Key(1);
        var newKey = Key(2);
        var underOldKey = AesGcmSecretBox.Protect("written-before-rotation", 1, oldKey);
        var underNewKey = AesGcmSecretBox.Protect("written-after-rotation", 2, newKey);

        byte[]? Resolve(int v) => v switch { 1 => oldKey, 2 => newKey, _ => null };

        AesGcmSecretBox.Unprotect(underOldKey, Resolve).ShouldBe("written-before-rotation");
        AesGcmSecretBox.Unprotect(underNewKey, Resolve).ShouldBe("written-after-rotation");
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("YWJj")] // valid base64, far too short to be a blob
    public void Malformed_blobs_fail_cleanly(string blob)
    {
        Should.Throw<CryptographicException>(() => AesGcmSecretBox.Unprotect(blob, _ => Key(7)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("c2hvcnQ=")]  // base64 of "short" — not 32 bytes
    [InlineData("nope!")]     // not base64 at all
    public void DecodeKey_refuses_anything_that_is_not_aes256(string? configured)
    {
        var ex = Should.Throw<InvalidOperationException>(() => AesGcmSecretBox.DecodeKey(configured, "Some:Section"));

        // The message names the section, so a misconfiguration says where to fix it.
        ex.Message.ShouldContain("Some:Section");
    }

    [Fact]
    public void DecodeKey_accepts_a_valid_key()
    {
        var configured = Convert.ToBase64String(Key(3));

        AesGcmSecretBox.DecodeKey(configured, "Some:Section").ShouldBe(Key(3));
    }
}
