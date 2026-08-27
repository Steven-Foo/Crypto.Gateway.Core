using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Derivation;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Infrastructure.Secrets;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Options;
using NBitcoin;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.KeyManagement.Tests;

/// <summary>
/// Proves <see cref="DevHdWalletSigningSecretProvider"/> actually closes the real-signing gap: given the
/// SAME composite reference (<c>"{seedReference}#{index}"</c>) the platform/deposit signing-key directories
/// hand to <c>TronSigner</c>, it must produce the exact private key controlling the address already derived
/// (watch-only) at that index — money-critical, since signing with the wrong key would either fail on-chain
/// or, worse, sign for a different address entirely. Every other reference must pass straight through to the
/// wrapped store, unchanged.
/// </summary>
public sealed class DevHdWalletSigningSecretProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Bip32Secp256k1KeyDeriver WatchOnly = new();

    private static DevHdWalletSigningSecretProvider Provider(
        MutableInMemorySecretStore? store = null, DevelopmentKeyCustodyOptions? options = null) =>
        new(store ?? new MutableInMemorySecretStore(), Options.Create(options ?? new DevelopmentKeyCustodyOptions()));

    private static DevHdWalletProvisioner ProvisionerFor(
        MutableInMemorySecretStore store, DevelopmentKeyCustodyOptions? options = null) =>
        new(store, TimeProvider.System, Options.Create(options ?? new DevelopmentKeyCustodyOptions()));

    [Fact]
    public async Task Derives_the_exact_private_key_controlling_the_merchant_deposit_address_at_that_index()
    {
        var merchantId = Guid.CreateVersion7();
        var store = new MutableInMemorySecretStore();
        var accountXpub = ProvisionerFor(store).ResolveMerchantDepositXpub(merchantId, Chain.Tron);
        var expectedPublicKey = WatchOnly.DerivePublicKey(accountXpub, index: 3);

        var reference = $"dev:hdwallet:{merchantId:N}:Tron:seed#3";
        using var lease = await Provider(store).GetAsync(reference, Ct);

        var derivedPublicKey = new Key(lease.Value.ToArray()).PubKey.Decompress().ToBytes();
        derivedPublicKey.ShouldBe(expectedPublicKey);
    }

    [Fact]
    public async Task Derives_the_exact_private_key_controlling_the_platform_withdrawal_pool_address_at_that_index()
    {
        var store = new MutableInMemorySecretStore();
        var accountXpub = ProvisionerFor(store).ResolvePlatformWithdrawalXpub(Chain.Tron);
        var expectedPublicKey = WatchOnly.DerivePublicKey(accountXpub, index: 1);

        var reference = "dev:hdwallet:platform:withdrawal:Tron:seed#1";
        using var lease = await Provider(store).GetAsync(reference, Ct);

        var derivedPublicKey = new Key(lease.Value.ToArray()).PubKey.Decompress().ToBytes();
        derivedPublicKey.ShouldBe(expectedPublicKey);
    }

    [Fact]
    public async Task A_configured_merchant_xpub_override_refuses_to_derive_a_private_key()
    {
        var merchantId = Guid.CreateVersion7();
        var options = new DevelopmentKeyCustodyOptions { DevMerchantXpub = SampleXpub() };

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await Provider(options: options).GetAsync($"dev:hdwallet:{merchantId:N}:Tron:seed#0", Ct));
    }

    [Fact]
    public async Task A_configured_withdrawal_xpub_override_refuses_to_derive_a_private_key()
    {
        var options = new DevelopmentKeyCustodyOptions { DevWithdrawalXpub = SampleXpub() };

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await Provider(options: options).GetAsync("dev:hdwallet:platform:withdrawal:Tron:seed#0", Ct));
    }

    [Fact]
    public async Task A_plain_reference_without_an_index_passes_through_to_the_wrapped_store_unchanged()
    {
        var store = new MutableInMemorySecretStore();
        store.Put("dev/tron/deposit/xpub", "some-public-xpub-value");

        using var lease = await Provider(store).GetAsync("dev/tron/deposit/xpub", Ct);

        lease.AsPublicUtf8String().ShouldBe("some-public-xpub-value");
    }

    [Fact]
    public async Task An_imported_platform_key_reference_passes_through_to_the_wrapped_store_unchanged()
    {
        var store = new MutableInMemorySecretStore();
        store.Put("dev-energy-hub-tron", "aabbccdd");

        using var lease = await Provider(store).GetAsync("dev-energy-hub-tron", Ct);

        lease.AsPublicUtf8String().ShouldBe("aabbccdd");
    }

    [Fact]
    public async Task A_reference_that_looks_like_a_composite_key_but_isnt_dev_hdwallet_shaped_falls_through()
    {
        // Has a '#' and a numeric suffix, but doesn't start with "dev:hdwallet:" — must not be misread.
        var store = new MutableInMemorySecretStore();
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await Provider(store).GetAsync("some:other:reference#0", Ct));
    }

    private static string SampleXpub() =>
        ExtKey.CreateFromSeed(new Mnemonic(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
                Wordlist.English).DeriveSeed())
            .Derive(new KeyPath("44'/195'/0'/0")).Neuter().ToString(Network.Main);
}
