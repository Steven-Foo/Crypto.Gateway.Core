using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using NBitcoin;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Derivation is anchored to <b>published</b> BIP-39/BIP-44 vectors, never to whatever this code
/// happens to emit. If a dependency upgrade changes any of these outputs, every deposit address the
/// gateway ever hands out changes with it — and funds sent to the old ones become unspendable.
/// </summary>
public sealed class Bip32DerivationTests
{
    /// <summary>The canonical BIP-39 test mnemonic.</summary>
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>Published BIP-39 vector: seed for the above mnemonic with an empty passphrase.</summary>
    private const string ExpectedSeedHex =
        "5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc1" +
        "9a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4";

    /// <summary>Published BIP-44 vector: m/44'/60'/0'/0/0 of the above mnemonic.</summary>
    private const string ExpectedEthereumAddress = "0x9858EfFD232B4033E47d90003D41EC34EcaEda94";

    private static ExtKey Master() => ExtKey.CreateFromSeed(new Mnemonic(TestMnemonic, Wordlist.English).DeriveSeed());

    /// <summary>The xpub at the branch level — exactly what we keep in the secret store.</summary>
    private static string BranchXpub(string branchPath) =>
        Master().Derive(new KeyPath(branchPath)).Neuter().ToString(Network.Main);

    [Fact]
    public void Mnemonic_produces_the_published_bip39_seed()
    {
        var seed = new Mnemonic(TestMnemonic, Wordlist.English).DeriveSeed();

        Convert.ToHexString(seed).ToLowerInvariant().ShouldBe(ExpectedSeedHex);
    }

    [Fact]
    public void Ethereum_address_matches_the_published_bip44_vector()
    {
        var publicKey = new Bip32Secp256k1KeyDeriver().DerivePublicKey(BranchXpub("44'/60'/0'/0"), index: 0);
        var address = new EthereumAddressEncoder().Encode(publicKey);

        address.ShouldBe(ExpectedEthereumAddress);
    }

    /// <summary>Also proves EIP-55: the mixed case must match the published vector exactly.</summary>
    [Fact]
    public void Ethereum_address_carries_a_correct_eip55_checksum()
    {
        var publicKey = new Bip32Secp256k1KeyDeriver().DerivePublicKey(BranchXpub("44'/60'/0'/0"), index: 0);
        var address = new EthereumAddressEncoder().Encode(publicKey);

        address.ShouldBe(ExpectedEthereumAddress);                                  // exact case
        address.ShouldNotBe(ExpectedEthereumAddress.ToLowerInvariant());            // not all-lower
        address[2..].ShouldNotBe(address[2..].ToUpperInvariant());                  // not all-upper
    }

    /// <summary>
    /// TRON at m/44'/195'/0'/0/0. Every component of this pipeline is independently pinned
    /// elsewhere — BIP-32 and keccak-20 by the Ethereum vector above, coin type 195 by SLIP-44, and
    /// Base58Check by the USDT-TRC20 contract round-trip in <c>AddressEncoderTests</c> — so this
    /// value is a regression lock on their composition.
    /// </summary>
    [Fact]
    public void Tron_address_is_stable_and_well_formed()
    {
        var publicKey = new Bip32Secp256k1KeyDeriver().DerivePublicKey(BranchXpub("44'/195'/0'/0"), index: 0);
        var address = new TronAddressEncoder().Encode(publicKey);

        address.ShouldBe("TUEZSdKsoDHQMeZwihtdoBiN46zxhGWYdH");
        address.ShouldStartWith("T");

        var raw = TronAddressEncoder.ToRawAddress(address); // validates the checksum
        raw.Length.ShouldBe(21);
        raw[0].ShouldBe(TronAddressEncoder.MainnetPrefix);
    }

    /// <summary>
    /// The property the whole watch-only design rests on: an xpub derives the same public key the
    /// seed would. If this were ever false, addresses we hand out could not be signed for.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(2147483647)] // the last non-hardened index
    public void Xpub_derivation_agrees_with_seed_derivation(long index)
    {
        const string branch = "44'/195'/0'/0";

        var fromSeed = Master().Derive(new KeyPath($"{branch}/{index}")).PrivateKey.PubKey.Decompress().ToBytes();
        var fromXpub = new Bip32Secp256k1KeyDeriver().DerivePublicKey(BranchXpub(branch), index);

        fromXpub.ShouldBe(fromSeed);
    }

    [Fact]
    public void Different_indices_produce_different_addresses()
    {
        var deriver = new Bip32Secp256k1KeyDeriver();
        var encoder = new TronAddressEncoder();
        var xpub = BranchXpub("44'/195'/0'/0");

        var addresses = Enumerable.Range(0, 50)
            .Select(i => encoder.Encode(deriver.DerivePublicKey(xpub, i)))
            .ToList();

        addresses.Distinct().Count().ShouldBe(50);
    }

    [Fact]
    public void Deriving_the_same_index_twice_is_deterministic()
    {
        var deriver = new Bip32Secp256k1KeyDeriver();
        var xpub = BranchXpub("44'/60'/0'/0");

        deriver.DerivePublicKey(xpub, 7).ShouldBe(deriver.DerivePublicKey(xpub, 7));
    }

    [Fact]
    public void The_derived_public_key_is_65_byte_uncompressed()
    {
        var publicKey = new Bip32Secp256k1KeyDeriver().DerivePublicKey(BranchXpub("44'/60'/0'/0"), 0);

        publicKey.Length.ShouldBe(65);
        publicKey[0].ShouldBe((byte)0x04);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2147483648)] // 2^31 — BIP-32 would read this as a hardened index
    public void Deriver_rejects_an_index_outside_the_non_hardened_range(long index) =>
        Should.Throw<ArgumentOutOfRangeException>(
            () => new Bip32Secp256k1KeyDeriver().DerivePublicKey(BranchXpub("44'/60'/0'/0"), index));

    /// <summary>
    /// Independently confirms the constraint that forced the per-chain derivation scheme: a public
    /// key cannot produce a hardened child. Solana's path is hardened at every level, which is why
    /// it can never use watch-only derivation.
    /// </summary>
    [Fact]
    public void An_xpub_cannot_derive_a_hardened_child()
    {
        var xpub = ExtPubKey.Parse(BranchXpub("44'/60'/0'/0"), Network.Main);

        Should.Throw<InvalidOperationException>(() => xpub.Derive(0x8000_0000u));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-xpub")]
    public void Deriver_rejects_a_malformed_account_public_key(string xpub) =>
        Should.Throw<Exception>(() => new Bip32Secp256k1KeyDeriver().DerivePublicKey(xpub, 0));
}
