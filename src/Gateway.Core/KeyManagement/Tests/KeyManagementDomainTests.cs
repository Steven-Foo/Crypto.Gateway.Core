using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

public sealed class DerivationPathTests
{
    [Theory]
    [InlineData(Chain.Ethereum, "m/44'/60'/0'/0")]
    [InlineData(Chain.Tron, "m/44'/195'/0'/0")]
    [InlineData(Chain.Tron, "m/44'/195'/3'/0")]
    [InlineData(Chain.Tron, "m/44h/195h/0h/0")] // 'h' is an accepted hardened marker
    public void Accepts_a_well_formed_secp256k1_branch_path(Chain chain, string path)
    {
        var result = DerivationPath.Create(path, chain);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Scheme.ShouldBe(DerivationScheme.Bip32Secp256k1);
        result.Value.Chain.ShouldBe(chain);
    }

    [Fact]
    public void Accepts_a_well_formed_ed25519_account_root_path()
    {
        var result = DerivationPath.Create("m/44'/501'", Chain.Solana);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Scheme.ShouldBe(DerivationScheme.Slip10Ed25519);
    }

    /// <summary>
    /// The defect this type exists to prevent: an Ethereum wallet carrying TRON's coin type. Both
    /// are secp256k1, so it would derive perfectly valid addresses we could never spend from.
    /// </summary>
    [Theory]
    [InlineData(Chain.Ethereum, "m/44'/195'/0'/0")]
    [InlineData(Chain.Tron, "m/44'/60'/0'/0")]
    [InlineData(Chain.Solana, "m/44'/60'")]
    public void Rejects_a_coin_type_that_does_not_match_the_chain(Chain chain, string path) =>
        DerivationPath.Create(path, chain).Error!.Code.ShouldBe(KeyManagementErrors.PathCoinTypeMismatch.Code);

    [Theory]
    [InlineData("m/49'/60'/0'/0")] // BIP-49, not BIP-44
    [InlineData("m/0'/60'/0'/0")]
    public void Rejects_a_path_that_is_not_bip44(string path) =>
        DerivationPath.Create(path, Chain.Ethereum).Error!.Code.ShouldBe(KeyManagementErrors.PathPurposeNotBip44.Code);

    [Theory]
    [InlineData("m/44'/60'/0'")]        // too few levels for secp256k1
    [InlineData("m/44'/60'/0'/0/0")]    // that is an address path, not a branch path
    public void Rejects_a_secp256k1_path_of_the_wrong_depth(string path) =>
        DerivationPath.Create(path, Chain.Ethereum).Error!.Code.ShouldBe(KeyManagementErrors.PathShapeInvalid.Code);

    /// <summary>A hardened change level makes CKDpub impossible — the xpub could derive nothing.</summary>
    [Fact]
    public void Rejects_a_hardened_change_level() =>
        DerivationPath.Create("m/44'/60'/0'/0'", Chain.Ethereum).Error!.Code
            .ShouldBe(KeyManagementErrors.PathShapeInvalid.Code);

    [Fact]
    public void Rejects_a_non_hardened_account_level() =>
        DerivationPath.Create("m/44'/60'/0/0", Chain.Ethereum).Error!.Code
            .ShouldBe(KeyManagementErrors.PathShapeInvalid.Code);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejects_an_empty_path(string path) =>
        DerivationPath.Create(path, Chain.Ethereum).Error!.Code.ShouldBe(KeyManagementErrors.PathRequired.Code);

    [Theory]
    [InlineData("44'/60'/0'/0")]    // missing the m/ root
    [InlineData("m/44'/60'/x'/0")]
    [InlineData("m/44'/60'/0'/-1")]
    [InlineData("m")]
    public void Rejects_a_malformed_path(string path) =>
        DerivationPath.Create(path, Chain.Ethereum).Error!.Code.ShouldBe(KeyManagementErrors.PathMalformed.Code);

    [Fact]
    public void Builds_the_address_path_for_secp256k1_by_appending_the_index() =>
        DerivationPath.Create("m/44'/195'/0'/0", Chain.Tron).Value
            .AddressPathFor(17).Value.ShouldBe("m/44'/195'/0'/0/17");

    /// <summary>Solana's address levels are hardened and appended at derivation time.</summary>
    [Fact]
    public void Builds_the_address_path_for_ed25519_with_hardened_levels() =>
        DerivationPath.Create("m/44'/501'", Chain.Solana).Value
            .AddressPathFor(17).Value.ShouldBe("m/44'/501'/17'/0'");

    [Theory]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void Refuses_to_build_an_address_path_outside_the_non_hardened_range(long index) =>
        DerivationPath.Create("m/44'/60'/0'/0", Chain.Ethereum).Value
            .AddressPathFor(index).Error!.Code.ShouldBe(KeyManagementErrors.IndexOutOfRange.Code);

    [Fact]
    public void Max_index_is_the_last_non_hardened_index()
    {
        DerivationPath.MaxIndex.ShouldBe(2147483647);
        DerivationPath.IsIndexInRange(DerivationPath.MaxIndex).ShouldBeTrue();
        DerivationPath.IsIndexInRange(DerivationPath.MaxIndex + 1).ShouldBeFalse();
    }

    [Fact]
    public void Slip44_coin_types_are_the_registered_ones()
    {
        DerivationPath.CoinTypeFor(Chain.Ethereum).ShouldBe(60);
        DerivationPath.CoinTypeFor(Chain.Tron).ShouldBe(195);
        DerivationPath.CoinTypeFor(Chain.Solana).ShouldBe(501);
    }
}

public sealed class HdWalletDomainTests
{
    private static Result<HdWallet> Create(
        Chain chain = Chain.Tron,
        string path = "m/44'/195'/0'/0",
        string? publicKeyReference = "secret://xpub") =>
        HdWallet.Create(
            "Deposit pool", chain, HdWalletPurpose.Deposit,
            SecretProviderKind.AwsSecretsManager, "arn:aws:secretsmanager:seed",
            publicKeyReference, path);

    [Fact]
    public void Creates_an_active_wallet_starting_at_index_zero()
    {
        var wallet = Create().Value;

        wallet.IsActive.ShouldBeTrue();
        wallet.NextDerivationIndex.ShouldBe(0);
        wallet.Scheme.ShouldBe(DerivationScheme.Bip32Secp256k1);
        wallet.SupportsWatchOnlyDerivation.ShouldBeTrue();
        wallet.IsExhausted.ShouldBeFalse();
        wallet.DerivationPath.Value.ShouldBe("m/44'/195'/0'/0");
    }

    [Fact]
    public void A_secp256k1_wallet_requires_an_account_public_key_reference() =>
        Create(publicKeyReference: null).Error!.Code.ShouldBe(KeyManagementErrors.PublicKeyReferenceRequired.Code);

    /// <summary>
    /// An ed25519 wallet carrying an xpub reference would imply watch-only derivation is possible.
    /// It is not — SLIP-0010 is hardened-only — so the wallet must not pretend otherwise.
    /// </summary>
    [Fact]
    public void An_ed25519_wallet_must_not_carry_a_public_key_reference() =>
        Create(Chain.Solana, "m/44'/501'", "secret://xpub").Error!.Code
            .ShouldBe(KeyManagementErrors.PublicKeyReferenceNotApplicable.Code);

    [Fact]
    public void An_ed25519_wallet_is_valid_without_one()
    {
        var wallet = Create(Chain.Solana, "m/44'/501'", null).Value;

        wallet.Scheme.ShouldBe(DerivationScheme.Slip10Ed25519);
        wallet.SupportsWatchOnlyDerivation.ShouldBeFalse();
    }

    [Fact]
    public void Requires_a_secret_reference_because_the_seed_is_never_stored() =>
        HdWallet.Create("n", Chain.Tron, HdWalletPurpose.Deposit, SecretProviderKind.Hsm, "  ", "x", "m/44'/195'/0'/0")
            .Error!.Code.ShouldBe(KeyManagementErrors.SecretReferenceRequired.Code);

    [Fact]
    public void Requires_a_name() =>
        HdWallet.Create("  ", Chain.Tron, HdWalletPurpose.Deposit, SecretProviderKind.Hsm, "ref", "x", "m/44'/195'/0'/0")
            .Error!.Code.ShouldBe(KeyManagementErrors.NameRequired.Code);

    [Fact]
    public void Rejects_a_path_whose_coin_type_contradicts_the_chain() =>
        Create(Chain.Ethereum, "m/44'/195'/0'/0").Error!.Code.ShouldBe(KeyManagementErrors.PathCoinTypeMismatch.Code);

    [Fact]
    public void Derives_a_key_recording_the_full_reproducible_path()
    {
        var wallet = Create().Value;

        var key = wallet.DeriveKey(42, "TSomeAddress", DateTimeOffset.UtcNow).Value;

        key.HdWalletId.ShouldBe(wallet.Id);
        key.DerivationIndex.ShouldBe(42);
        key.Chain.ShouldBe(Chain.Tron);
        key.DerivationPath.ShouldBe("m/44'/195'/0'/0/42"); // an operator holding the mnemonic can reproduce it
    }

    [Fact]
    public void Refuses_to_derive_a_key_beyond_the_non_hardened_range() =>
        Create().Value.DeriveKey(DerivationPath.MaxIndex + 1, "T", DateTimeOffset.UtcNow)
            .Error!.Code.ShouldBe(KeyManagementErrors.IndexOutOfRange.Code);

    [Fact]
    public void Refuses_to_derive_from_a_disabled_wallet()
    {
        var wallet = Create().Value;
        wallet.Disable(DateTimeOffset.UtcNow);

        wallet.DeriveKey(0, "T", DateTimeOffset.UtcNow).Error!.Code.ShouldBe(KeyManagementErrors.NotActive.Code);
    }

    [Fact]
    public void An_archived_wallet_is_no_longer_active()
    {
        var wallet = Create().Value;
        wallet.Archive(DateTimeOffset.UtcNow);

        wallet.IsActive.ShouldBeFalse();
        wallet.Status.ShouldBe(HdWalletStatus.Archived);
    }
}

public sealed class SecretLeaseTests
{
    [Fact]
    public void Exposes_the_secret_until_disposed()
    {
        var lease = new SecretLease("seed-material"u8.ToArray());

        lease.Value.ToArray().ShouldBe("seed-material"u8.ToArray());
    }

    /// <summary>
    /// The buffer is wiped, not merely dropped. This is why secrets are never held as strings:
    /// a string could not be overwritten here at all.
    /// </summary>
    [Fact]
    public void Zeroes_the_buffer_on_dispose()
    {
        var buffer = Encoding.UTF8.GetBytes("very-secret-seed");
        var lease = new SecretLease(buffer);

        lease.Dispose();

        buffer.ShouldAllBe(b => b == 0);
    }

    [Fact]
    public void Reading_a_disposed_lease_throws_rather_than_returning_stale_bytes()
    {
        var lease = new SecretLease("x"u8.ToArray());
        lease.Dispose();

        Should.Throw<ObjectDisposedException>(() => lease.Value.ToArray());
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        var lease = new SecretLease("x"u8.ToArray());

        lease.Dispose();
        Should.NotThrow(lease.Dispose);
    }

    [Fact]
    public void Public_material_can_be_read_as_a_string()
    {
        using var lease = new SecretLease("xpub6ABC"u8.ToArray());

        lease.AsPublicUtf8String().ShouldBe("xpub6ABC");
    }

    [Fact]
    public void Zeroing_uses_the_cryptographic_helper_semantics()
    {
        // Guards against someone "optimising" ZeroMemory into a no-op for short buffers.
        var buffer = RandomNumberGenerator.GetBytes(32);
        buffer.Any(b => b != 0).ShouldBeTrue();

        new SecretLease(buffer).Dispose();

        buffer.ShouldAllBe(b => b == 0);
    }
}
