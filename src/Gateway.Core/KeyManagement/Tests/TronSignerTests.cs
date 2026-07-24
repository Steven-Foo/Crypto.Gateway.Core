using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Signing;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// The real TRON signer. secp256k1 signing is money-critical: a wrong signature is rejected by the node
/// (best case) or authorises the wrong transfer (worst). These lock the format — <c>r ‖ s ‖ recId</c> over
/// <c>sha256(raw_data)</c> — with a published key vector and a sign→recover round-trip, and prove the
/// integrity guard that refuses to sign a txID that doesn't match its raw_data.
/// </summary>
public sealed class TronSignerTests
{
    // secp256k1 private key = 1 → its public key is the generator point G (a rock-solid, published vector).
    private const string PrivateKeyOneHex = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string GeneratorPubKeyCompressed = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const string KeyReference = "kms://tron/hot/0";

    private static byte[] PrivateKeyOne() => Convert.FromHexString(PrivateKeyOneHex);

    [Fact]
    public void Private_key_one_derives_the_known_generator_public_key() =>
        new Key(PrivateKeyOne()).PubKey.ToHex().ShouldBe(GeneratorPubKeyCompressed);

    [Fact]
    public void The_signature_is_65_bytes_and_recovers_the_signing_key()
    {
        var key = new Key(PrivateKeyOne());
        var hash = SHA256.HashData("a tron transaction id preimage"u8.ToArray());

        var signature = TronSigner.SignRecoverable(PrivateKeyOne(), hash);

        signature.Length.ShouldBe(65);
        signature[64].ShouldBeLessThan((byte)4); // recovery id 0..3

        // Recover the public key from r‖s‖recId over the same hash — must be the signer's key. This is
        // exactly the check the TRON node performs, so a recovery match means the node accepts the signature.
        var compact = new CompactSignature(signature[64], signature[..64]);
        PubKey.RecoverCompact(new uint256(hash), compact).ToHex().ShouldBe(key.PubKey.ToHex());
    }

    [Fact]
    public void The_signature_is_low_s_canonical()
    {
        // TRON (like Bitcoin/Ethereum) rejects a malleable high-S signature. s must be <= n/2.
        var signature = TronSigner.SignRecoverable(PrivateKeyOne(), SHA256.HashData("low-s check"u8.ToArray()));

        var s = new System.Numerics.BigInteger(signature[32..64], isUnsigned: true, isBigEndian: true);
        var halfOrder = System.Numerics.BigInteger.Parse("57896044618658097711785492504343953926418782139537452191302581570759080747168");
        s.ShouldBeLessThanOrEqualTo(halfOrder);
    }

    [Fact]
    public async Task SignAsync_signs_a_consistent_transaction_and_appends_a_recoverable_signature()
    {
        var rawData = Convert.FromHexString("0a02deadbeef1234567890abcdef");
        var txId = Convert.ToHexString(SHA256.HashData(rawData)).ToLowerInvariant();
        var unsigned = UnsignedTx(txId, Convert.ToHexString(rawData).ToLowerInvariant());

        var signer = SignerWith(PrivateKeyOneHex);
        var result = await signer.SignAsync(new SigningRequest(Guid.CreateVersion7(), Chain.Tron, unsigned, KeyReference), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var signed = JsonNode.Parse(Encoding.UTF8.GetString(result.Value.SignedPayload))!.AsObject();
        var sigHex = signed["signature"]!.AsArray()[0]!.GetValue<string>();
        sigHex.Length.ShouldBe(130); // 65 bytes

        // The appended signature must recover the signing key over sha256(raw_data) — i.e. the node will accept it.
        var sig = Convert.FromHexString(sigHex);
        var compact = new CompactSignature(sig[64], sig[..64]);
        PubKey.RecoverCompact(new uint256(SHA256.HashData(rawData)), compact).ToHex()
            .ShouldBe(new Key(PrivateKeyOne()).PubKey.ToHex());
    }

    [Fact]
    public async Task SignAsync_refuses_when_the_txid_does_not_match_raw_data()
    {
        var rawData = Convert.FromHexString("0a02cafe");
        var wrongTxId = new string('a', 64); // not sha256(raw_data)
        var unsigned = UnsignedTx(wrongTxId, Convert.ToHexString(rawData).ToLowerInvariant());

        var result = await SignerWith(PrivateKeyOneHex)
            .SignAsync(new SigningRequest(Guid.CreateVersion7(), Chain.Tron, unsigned, KeyReference), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("sign.txid_mismatch");
    }

    [Fact]
    public async Task SignAsync_refuses_a_non_tron_chain()
    {
        var result = await SignerWith(PrivateKeyOneHex).SignAsync(
            new SigningRequest(Guid.CreateVersion7(), Chain.Ethereum, "{}"u8.ToArray(), KeyReference), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("sign.unsupported_chain");
    }

    [Fact]
    public async Task SignAsync_reports_a_missing_key()
    {
        var rawData = Convert.FromHexString("0a02beef");
        var unsigned = UnsignedTx(Convert.ToHexString(SHA256.HashData(rawData)).ToLowerInvariant(), Convert.ToHexString(rawData).ToLowerInvariant());

        var signer = new TronSigner(InMemorySecretProvider.FromStrings(new Dictionary<string, string>()), NullLogger<TronSigner>.Instance);
        var result = await signer.SignAsync(new SigningRequest(Guid.CreateVersion7(), Chain.Tron, unsigned, KeyReference), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("sign.key_not_found");
    }

    private static TronSigner SignerWith(string privateKeyHex) =>
        new(InMemorySecretProvider.FromStrings(new Dictionary<string, string> { [KeyReference] = privateKeyHex }),
            NullLogger<TronSigner>.Instance);

    private static byte[] UnsignedTx(string txId, string rawDataHex)
    {
        var obj = new JsonObject
        {
            ["txID"] = txId,
            ["raw_data"] = new JsonObject { ["expiration"] = 1 },
            ["raw_data_hex"] = rawDataHex,
        };
        return Encoding.UTF8.GetBytes(obj.ToJsonString());
    }
}
