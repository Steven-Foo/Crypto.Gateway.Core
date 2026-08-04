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
    private const string KeyReference = "tron-hot-wallet-0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly IPlatformSigningKeyDirectory _signingKeys = Substitute.For<IPlatformSigningKeyDirectory>();

    private TreasuryHotWalletDirectory NewDirectory() => new(_wallets, _signingKeys);

    private void WithWallets(params PlatformWallet[] wallets) =>
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Ct).Returns(wallets);

    private void WithSigningKey(string? keyReference) =>
        _signingKeys.FindActiveAsync(Chain.Tron, DerivationPurpose.Withdrawal, Ct)
            .Returns(keyReference is null ? null : new PlatformSigningKey(Guid.NewGuid(), Chain.Tron, keyReference));

    [Fact]
    public async Task Combines_the_single_hot_wallet_address_with_the_signing_key_reference()
    {
        WithWallets(new PlatformWallet(Guid.NewGuid(), Chain.Tron, Address, "HotWithdrawal"));
        WithSigningKey(KeyReference);

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Address.ShouldBe(Address);
        result.Value.KeyReference.ShouldBe(KeyReference);
    }

    [Fact]
    public async Task Ignores_non_hot_wallets_when_selecting()
    {
        WithWallets(
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TColdAddr", "Cold"),
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, Address, "HotWithdrawal"),
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TEnergyAddr", "Energy"));
        WithSigningKey(KeyReference);

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Address.ShouldBe(Address);
    }

    [Fact]
    public async Task No_hot_wallet_registered_fails()
    {
        WithWallets(new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TColdAddr", "Cold"));

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(TreasuryErrors.HotWalletNotConfigured.Code);
    }

    [Fact]
    public async Task More_than_one_hot_wallet_is_ambiguous_and_refused()
    {
        WithWallets(
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, Address, "HotWithdrawal"),
            new PlatformWallet(Guid.NewGuid(), Chain.Tron, "TSecondHot", "HotWithdrawal"));

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(TreasuryErrors.HotWalletAmbiguous.Code);
    }

    [Fact]
    public async Task A_hot_wallet_without_a_registered_signing_key_fails()
    {
        WithWallets(new PlatformWallet(Guid.NewGuid(), Chain.Tron, Address, "HotWithdrawal"));
        WithSigningKey(null);

        var result = await NewDirectory().GetHotWalletAsync(Chain.Tron, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(TreasuryErrors.SigningKeyMissing.Code);
    }
}
