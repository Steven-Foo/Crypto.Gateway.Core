using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Tests;

public sealed class TreasuryHotWalletDirectoryTests
{
    private const string Address = "TAueoxR1rwogpLDjYJzB7GGYYWgPbtajSs";
    private const string KeyReference = "tron-hot-wallet-0#0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly IPlatformSigningKeyDirectory _signingKeys = Substitute.For<IPlatformSigningKeyDirectory>();

    private TreasuryHotWalletDirectory NewDirectory() => new(_wallets, _signingKeys);

    private void WithWallets(params PlatformWallet[] wallets) =>
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(wallets);

    private void WithKeyForAddress(string address, string? keyReference) =>
        _signingKeys.FindByAddressAsync(Chain.Tron, address, Arg.Any<CancellationToken>())
            .Returns(keyReference is null ? null : new PlatformSigningKey(Guid.NewGuid(), Chain.Tron, keyReference));

    private static PlatformWallet Hot(string address) => new(Guid.NewGuid(), Chain.Tron, address, "HotWithdrawal");

    [Fact]
    public async Task Combines_a_hot_wallet_address_with_its_per_address_signing_key()
    {
        WithWallets(Hot(Address));
        WithKeyForAddress(Address, KeyReference);

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Address.ShouldBe(Address);
        result.Value.KeyReference.ShouldBe(KeyReference);
    }

    [Fact]
    public async Task Ignores_non_hot_wallets_when_building_the_pool()
    {
        WithWallets(
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TColdAddr", "Cold"),
            Hot(Address),
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TEnergyAddr", "Energy"));
        WithKeyForAddress(Address, KeyReference);

        var pool = await NewDirectory().GetHotWalletPoolAsync(Chain.Tron, Ct);

        pool.Count.ShouldBe(1);
        pool[0].Address.ShouldBe(Address);
    }

    [Fact]
    public async Task No_hot_wallet_registered_fails_the_single_lookup()
    {
        WithWallets(new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TColdAddr", "Cold"));

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(TreasuryErrors.HotWalletNotConfigured.Code);
    }

    [Fact]
    public async Task The_pool_returns_every_hot_wallet_and_the_single_lookup_returns_the_first()
    {
        WithWallets(Hot(Address), Hot("TSecondHot"));
        WithKeyForAddress(Address, "refA#0");
        WithKeyForAddress("TSecondHot", "refB#1");

        var pool = await NewDirectory().GetHotWalletPoolAsync(Chain.Tron, Ct);
        pool.Count.ShouldBe(2);

        // Deterministic by ordinal address: 'A' (Address) sorts before 'S' (TSecondHot).
        var single = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);
        single.Value.Address.ShouldBe(Address);
    }

    [Fact]
    public async Task A_hot_wallet_without_a_resolvable_key_is_skipped()
    {
        WithWallets(Hot(Address));
        WithKeyForAddress(Address, null);

        (await NewDirectory().GetHotWalletPoolAsync(Chain.Tron, Ct)).ShouldBeEmpty();

        var single = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);
        single.IsFailure.ShouldBeTrue();
        single.Error!.Code.ShouldBe(TreasuryErrors.HotWalletNotConfigured.Code);
    }
}
