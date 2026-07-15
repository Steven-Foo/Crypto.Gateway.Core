using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using MerchantEntity = CryptoPaymentEngine.Gateway.Core.Merchant.Domain.Merchant;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// Inbound signature verification and outbound callback signing. Proves the partner's exact HMAC scheme
/// (<c>HMAC-SHA256(hexDecode(secret), "{timestamp}\n{body}")</c>) works end to end, that the secret is
/// recovered only through the cipher, and that a bad signature or a non-transactable merchant is refused.
/// </summary>
public sealed class MerchantSigningTests
{
    private const string ApiKey = "cpe_signing_test";
    // 32-byte signing key as 64 hex chars — the shape the partner's SDK hex-decodes.
    private const string SigningSecretHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AesGcmSecretCipher NewCipher() =>
        new(Options.Create(new SigningSecretOptions
        {
            CurrentKeyVersion = 1,
            Keys = new Dictionary<int, string> { [1] = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()) },
        }));

    private static (IMerchantRepository Repo, MerchantEntity Merchant, AesGcmSecretCipher Cipher) Setup(bool active = true)
    {
        var cipher = NewCipher();
        var now = DateTimeOffset.UtcNow;
        var merchant = MerchantEntity.Create("SIGN-1", "Signer", null).Value;
        if (active) merchant.Activate(now);
        var credential = merchant.IssueCredential(ApiKey, "bearer-hash", 1, cipher.Protect(SigningSecretHex), now).Value;

        var repo = Substitute.For<IMerchantRepository>();
        repo.FindActiveCredentialAsync(ApiKey, Arg.Any<CancellationToken>()).Returns(credential);
        repo.FindActiveCredentialByMerchantAsync(merchant.Id, Arg.Any<CancellationToken>()).Returns(credential);
        repo.GetByIdAsync(merchant.Id, Arg.Any<CancellationToken>()).Returns(merchant);
        return (repo, merchant, cipher);
    }

    /// <summary>The scheme, computed independently of the module, so the test is a true oracle.</summary>
    private static string SignHex(string secretHex, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Convert.FromHexString(secretHex), Encoding.UTF8.GetBytes($"{timestamp}\n{body}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    [Fact]
    public async Task A_correct_signature_authenticates_and_returns_the_merchant()
    {
        var (repo, merchant, cipher) = Setup();
        var verifier = new MerchantRequestVerifier(repo, cipher);

        const string ts = "1700000000";
        const string body = """{"amount":"1000000"}""";
        var result = await verifier.VerifyAsync(ApiKey, ts, body, SignHex(SigningSecretHex, ts, body), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(merchant.Id);
    }

    [Fact]
    public async Task A_wrong_signature_is_refused()
    {
        var (repo, _, cipher) = Setup();
        var verifier = new MerchantRequestVerifier(repo, cipher);

        var result = await verifier.VerifyAsync(ApiKey, "1700000000", "body", "deadbeef", Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.InvalidCredentials.Code);
    }

    [Fact]
    public async Task A_tampered_body_breaks_the_signature()
    {
        var (repo, _, cipher) = Setup();
        var verifier = new MerchantRequestVerifier(repo, cipher);

        const string ts = "1700000000";
        var signature = SignHex(SigningSecretHex, ts, """{"amount":"1"}""");
        var result = await verifier.VerifyAsync(ApiKey, ts, """{"amount":"999999"}""", signature, Ct);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_api_key_is_refused()
    {
        var repo = Substitute.For<IMerchantRepository>(); // returns null for any key
        var verifier = new MerchantRequestVerifier(repo, NewCipher());

        var result = await verifier.VerifyAsync("cpe_unknown", "1700000000", "body", "abcd", Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.InvalidCredentials.Code);
    }

    [Fact]
    public async Task A_valid_signature_for_a_non_transactable_merchant_is_refused()
    {
        var (repo, _, cipher) = Setup(active: false); // Pending → cannot transact
        var verifier = new MerchantRequestVerifier(repo, cipher);

        const string ts = "1700000000";
        const string body = "body";
        var result = await verifier.VerifyAsync(ApiKey, ts, body, SignHex(SigningSecretHex, ts, body), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(MerchantErrors.NotTransactable.Code);
    }

    [Fact]
    public async Task A_signed_callback_verifies_with_the_same_key()
    {
        var (repo, merchant, cipher) = Setup();
        var signer = new MerchantCallbackSigner(repo, cipher, TimeProvider.System);
        var verifier = new MerchantRequestVerifier(repo, cipher);

        const string body = """{"transactionId":"abc","data":{"amount":"5"}}""";
        var signed = await signer.SignAsync(merchant.Id, body, Ct);
        signed.IsSuccess.ShouldBeTrue();

        // The merchant verifies a callback exactly as we verify their requests — same construction, same key.
        var roundTrip = await verifier.VerifyAsync(ApiKey, signed.Value.Timestamp, body, signed.Value.SignatureHex, Ct);
        roundTrip.IsSuccess.ShouldBeTrue();
    }
}
