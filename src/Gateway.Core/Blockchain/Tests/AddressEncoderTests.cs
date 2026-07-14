using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Tests;

/// <summary>
/// Address encoding is validated against published, independently-known values — not against
/// whatever this code happens to produce. A wrong encoder sends customer funds to an address we
/// cannot sign for.
/// </summary>
public sealed class AddressEncoderTests
{
    // The end-to-end mnemonic -> address vectors live in KeyManagement's derivation tests, where the
    // public key is actually derived rather than pasted in.

    // ── Base58 / Base58Check ─────────────────────────────────────────────────

    /// <summary>The USDT-TRC20 contract address — a well-known Base58Check ↔ hex pair.</summary>
    [Fact]
    public void Base58Check_round_trips_the_known_usdt_trc20_contract_address()
    {
        const string base58 = "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t";
        const string rawHex = "41a614f803b6fd780986a42c78ec9c7f77e6ded13c";

        Convert.ToHexString(Base58.DecodeCheck(base58)).ToLowerInvariant().ShouldBe(rawHex);
        Base58.EncodeCheck(Convert.FromHexString(rawHex)).ShouldBe(base58);
    }

    [Fact]
    public void Base58Check_rejects_a_corrupted_checksum()
    {
        var valid = Base58.EncodeCheck(Convert.FromHexString("41a614f803b6fd780986a42c78ec9c7f77e6ded13c"));
        var corrupted = valid[..^1] + (valid[^1] == 'a' ? 'b' : 'a');

        Should.Throw<FormatException>(() => Base58.DecodeCheck(corrupted));
    }

    [Fact]
    public void Base58_preserves_leading_zero_bytes_as_ones()
    {
        // Solana's System Program id is 32 zero bytes; it must render as thirty-two '1' characters,
        // not the empty string. This is the classic leading-zero bug in Base58 implementations.
        var thirtyTwoZeros = new byte[32];

        Base58.Encode(thirtyTwoZeros).ShouldBe(new string('1', 32));
        Base58.Decode(new string('1', 32)).ShouldBe(thirtyTwoZeros);
    }

    [Fact]
    public void Base58_round_trips_arbitrary_payloads()
    {
        var random = new Random(20260710);

        for (var i = 0; i < 200; i++)
        {
            var payload = new byte[random.Next(1, 64)];
            random.NextBytes(payload);

            Base58.Decode(Base58.Encode(payload)).ShouldBe(payload);
        }
    }

    [Fact]
    public void Base58_rejects_characters_outside_the_alphabet() =>
        Should.Throw<FormatException>(() => Base58.Decode("0OIl")); // the four excluded look-alikes

    // ── Solana ───────────────────────────────────────────────────────────────

    [Fact]
    public void Solana_encodes_the_system_program_id()
    {
        var encoder = new SolanaAddressEncoder();

        encoder.Encode(new byte[32]).ShouldBe("11111111111111111111111111111111");
        encoder.Chain.ShouldBe(Chain.Solana);
    }

    [Fact]
    public void Solana_rejects_a_public_key_of_the_wrong_length() =>
        Should.Throw<ArgumentException>(() => new SolanaAddressEncoder().Encode(new byte[31]));

    // ── secp256k1 input validation ───────────────────────────────────────────

    [Theory]
    [InlineData(64)]
    [InlineData(33)] // compressed form — must be rejected, not silently mis-hashed
    public void Secp256k1_encoders_reject_a_public_key_that_is_not_65_bytes(int length)
    {
        Should.Throw<ArgumentException>(() => new EthereumAddressEncoder().Encode(new byte[length]));
        Should.Throw<ArgumentException>(() => new TronAddressEncoder().Encode(new byte[length]));
    }

    [Fact]
    public void Secp256k1_encoders_reject_a_key_without_the_uncompressed_prefix()
    {
        var wrongPrefix = new byte[65];
        wrongPrefix[0] = 0x02;

        Should.Throw<ArgumentException>(() => new EthereumAddressEncoder().Encode(wrongPrefix));
    }

    // ── TRON raw-address helper ──────────────────────────────────────────────

    [Fact]
    public void Tron_raw_address_helper_returns_the_0x41_prefixed_form()
    {
        var raw = TronAddressEncoder.ToRawAddress("TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t");

        raw.Length.ShouldBe(21);
        raw[0].ShouldBe(TronAddressEncoder.MainnetPrefix);
        Convert.ToHexString(raw).ToLowerInvariant().ShouldBe("41a614f803b6fd780986a42c78ec9c7f77e6ded13c");
    }

    [Fact]
    public void Tron_raw_address_helper_rejects_a_non_tron_address()
    {
        // Valid Base58Check, but a Bitcoin-style 0x00 prefix rather than TRON's 0x41.
        var bitcoinish = Base58.EncodeCheck(new byte[21]);

        Should.Throw<FormatException>(() => TronAddressEncoder.ToRawAddress(bitcoinish));
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_resolves_registered_chains_and_reports_unsupported_ones()
    {
        var factory = new AddressEncoderFactory([new EthereumAddressEncoder(), new TronAddressEncoder()]);

        factory.Supports(Chain.Ethereum).ShouldBeTrue();
        factory.Supports(Chain.Tron).ShouldBeTrue();
        factory.Supports(Chain.Solana).ShouldBeFalse();

        factory.For(Chain.Tron).ShouldBeOfType<TronAddressEncoder>();
        Should.Throw<InvalidOperationException>(() => factory.For(Chain.Solana));
    }
}
