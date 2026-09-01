using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.SharedKernel.Tests;

/// <summary>
/// The shared credential primitives both identity modules (staff + merchant portal) now delegate to. These
/// used to be duplicated per module; the properties asserted here are what must never drift between them.
/// </summary>
public sealed class SecurityPrimitiveTests
{
    // ── Pbkdf2PasswordHash ──

    [Fact]
    public void A_password_verifies_against_its_own_hash_and_a_wrong_one_does_not()
    {
        var hash = Pbkdf2PasswordHash.Hash("correct horse battery staple");

        Pbkdf2PasswordHash.Verify("correct horse battery staple", hash).ShouldBeTrue();
        Pbkdf2PasswordHash.Verify("wrong password", hash).ShouldBeFalse();
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time_salting()
    {
        // A per-hash random salt: identical passwords must not produce identical stored hashes, or the store
        // would leak which accounts share a password.
        Pbkdf2PasswordHash.Hash("same-password").ShouldNotBe(Pbkdf2PasswordHash.Hash("same-password"));
    }

    [Fact]
    public void The_hash_records_its_own_iteration_count_so_the_work_factor_can_be_raised_later()
    {
        var parts = Pbkdf2PasswordHash.Hash("pw").Split('.');
        parts.Length.ShouldBe(3);
        int.Parse(parts[0]).ShouldBeGreaterThanOrEqualTo(210_000); // OWASP 2023 baseline, never silently lowered
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("abc.def")]              // too few parts
    [InlineData("notanumber.c2FsdA==.aGFzaA==")] // unparseable iteration count
    public void A_malformed_stored_hash_fails_verification_instead_of_throwing(string malformed) =>
        Pbkdf2PasswordHash.Verify("pw", malformed).ShouldBeFalse();

    // ── OpaqueToken ──

    [Fact]
    public void Each_generated_token_is_unique_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => OpaqueToken.Generate()).ToList();

        tokens.Distinct().Count().ShouldBe(tokens.Count);                 // CSPRNG, no collisions
        tokens.ShouldAllBe(t => !t.Contains('+') && !t.Contains('/') && !t.Contains('=')); // base64url, unpadded
        tokens.ShouldAllBe(t => t.Length >= 43);                          // 256 bits of entropy
    }

    [Fact]
    public void Hashing_is_stable_for_the_same_token_and_differs_across_tokens()
    {
        var token = OpaqueToken.Generate();

        // Stable: the validator hashes a presented token and looks it up against the stored hash.
        OpaqueToken.Sha256Hex(token).ShouldBe(OpaqueToken.Sha256Hex(token));
        OpaqueToken.Sha256Hex(token).ShouldNotBe(OpaqueToken.Sha256Hex(OpaqueToken.Generate()));
    }

    [Fact]
    public void The_stored_hash_is_lower_case_hex_and_never_the_raw_token()
    {
        var token = OpaqueToken.Generate();
        var hash = OpaqueToken.Sha256Hex(token);

        hash.Length.ShouldBe(64);                     // SHA-256 as hex
        hash.ShouldBe(hash.ToLowerInvariant());       // lookups compare lower-case hex
        hash.ShouldNotBe(token);                      // the raw token is never what gets persisted
    }
}
