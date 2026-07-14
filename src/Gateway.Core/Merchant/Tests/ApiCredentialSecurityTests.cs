using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

public sealed class ApiCredentialSecurityTests
{
    private static HmacApiSecretHasher Hasher(int currentVersion = 1, params (int Version, string Pepper)[] peppers)
    {
        var configured = peppers.Length > 0 ? peppers : [(1, "pepper-v1-abcdefghijklmnop")];

        return new HmacApiSecretHasher(Options.Create(new ApiCredentialOptions
        {
            CurrentHashVersion = currentVersion,
            Peppers = configured.ToDictionary(p => p.Version, p => p.Pepper),
        }));
    }

    [Fact]
    public void Hash_never_returns_the_secret_itself()
    {
        const string secret = "super-secret-value";
        var hash = Hasher().Hash(secret);

        hash.ShouldNotBe(secret);
        hash.ShouldNotContain(secret);
    }

    [Fact]
    public void Verify_accepts_the_correct_secret_and_rejects_a_wrong_one()
    {
        var hasher = Hasher();
        var hash = hasher.Hash("correct-horse");

        hasher.Verify("correct-horse", hash, 1).ShouldBeTrue();
        hasher.Verify("wrong-horse", hash, 1).ShouldBeFalse();
    }

    [Fact]
    public void Hashing_is_deterministic_for_the_same_pepper()
    {
        var hasher = Hasher();
        hasher.Hash("s").ShouldBe(hasher.Hash("s"));
    }

    [Fact]
    public void A_different_pepper_produces_a_different_hash_for_the_same_secret()
    {
        var a = Hasher(1, (1, "pepper-one")).Hash("same-secret");
        var b = Hasher(1, (1, "pepper-two")).Hash("same-secret");

        a.ShouldNotBe(b);
    }

    /// <summary>
    /// The whole point of HashVersion: after rotating the pepper, credentials hashed with the old
    /// one must keep verifying. Without this the rotation locks out every merchant.
    /// </summary>
    [Fact]
    public void A_credential_hashed_with_an_old_pepper_still_verifies_after_rotation()
    {
        const string secret = "merchant-secret";
        var beforeRotation = Hasher(1, (1, "pepper-v1")).Hash(secret);

        var afterRotation = Hasher(2, (1, "pepper-v1"), (2, "pepper-v2"));

        afterRotation.CurrentVersion.ShouldBe(2);
        afterRotation.Verify(secret, beforeRotation, version: 1).ShouldBeTrue();

        // and new hashes use the new pepper
        var newHash = afterRotation.Hash(secret);
        newHash.ShouldNotBe(beforeRotation);
        afterRotation.Verify(secret, newHash, version: 2).ShouldBeTrue();
    }

    [Fact]
    public void Verify_rejects_a_version_with_no_configured_pepper()
    {
        var hasher = Hasher();
        var hash = hasher.Hash("s");

        hasher.Verify("s", hash, version: 99).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("dG9vLXNob3J0")] // valid base64, wrong length
    public void Verify_rejects_a_malformed_hash_without_throwing(string malformed) =>
        Hasher().Verify("s", malformed, 1).ShouldBeFalse();

    [Fact]
    public void Verify_rejects_empty_input_without_throwing()
    {
        var hasher = Hasher();
        hasher.Verify("", hasher.Hash("s"), 1).ShouldBeFalse();
    }

    [Fact]
    public void Hasher_refuses_to_start_without_a_pepper_for_the_current_version()
    {
        var act = () => new HmacApiSecretHasher(Options.Create(new ApiCredentialOptions
        {
            CurrentHashVersion = 2,
            Peppers = new Dictionary<int, string> { [1] = "only-v1" },
        }));

        Should.Throw<InvalidOperationException>(act);
    }

    [Fact]
    public void Hasher_refuses_to_start_with_no_peppers_at_all()
    {
        var act = () => new HmacApiSecretHasher(Options.Create(new ApiCredentialOptions
        {
            CurrentHashVersion = 1,
            Peppers = new Dictionary<int, string>(),
        }));

        Should.Throw<InvalidOperationException>(act);
    }

    [Fact]
    public void Generated_credentials_are_unique_and_shaped_as_expected()
    {
        var generator = new ApiCredentialGenerator();

        var first = generator.Generate();
        var second = generator.Generate();

        first.ApiKey.ShouldStartWith("cpe_");
        first.ApiKey.ShouldNotBe(second.ApiKey);
        first.Secret.ShouldNotBe(second.Secret);

        // 32 bytes of entropy, base64url without padding.
        first.Secret.Length.ShouldBe(43);
        first.Secret.ShouldNotContain("+");
        first.Secret.ShouldNotContain("/");
        first.Secret.ShouldNotContain("=");
    }
}
