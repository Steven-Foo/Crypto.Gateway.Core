using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Tests;

public sealed class TreasuryHotWalletProvisioningServiceTests
{
    private const string Address = "TAueoxR1rwogpLDjYJzB7GGYYWgPbtajSs";
    private const string SecretReference = "tron-hot-wallet-0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformKeyRegistrar _keyRegistrar = Substitute.For<IPlatformKeyRegistrar>();
    private readonly IPlatformWalletRegistrar _walletRegistrar = Substitute.For<IPlatformWalletRegistrar>();

    private TreasuryHotWalletProvisioningService NewService() =>
        new(_keyRegistrar, _walletRegistrar, NullLogger<TreasuryHotWalletProvisioningService>.Instance);

    [Fact]
    public async Task Registers_the_key_first_then_the_wallet_with_the_returned_derived_key_id()
    {
        var derivedKeyId = Guid.NewGuid();
        _keyRegistrar.RegisterImportedKeyAsync(Chain.Tron, DerivationPurpose.Withdrawal, Address, SecretReference, Arg.Any<string?>(), Ct)
            .Returns(Result.Success(new RegisteredPlatformKey(derivedKeyId, Chain.Tron, Address, SecretReference)));
        _walletRegistrar.RegisterPlatformWalletAsync(derivedKeyId, Chain.Tron, Address, "HotWithdrawal", Arg.Any<string?>(), Ct)
            .Returns(Result.Success(new RegisteredPlatformWallet(Guid.NewGuid(), Chain.Tron, Address, "HotWithdrawal")));

        var result = await NewService().ProvisionHotWalletAsync(Chain.Tron, Address, SecretReference, "hot", Ct);

        result.IsSuccess.ShouldBeTrue();
        // The wallet is registered against the key's DerivedKeyId — proving key-then-wallet ordering.
        await _walletRegistrar.Received(1).RegisterPlatformWalletAsync(
            derivedKeyId, Chain.Tron, Address, "HotWithdrawal", Arg.Any<string?>(), Ct);
    }

    [Fact]
    public async Task A_failed_key_registration_short_circuits_and_never_touches_the_wallet()
    {
        _keyRegistrar.RegisterImportedKeyAsync(Chain.Tron, DerivationPurpose.Withdrawal, Address, SecretReference, Arg.Any<string?>(), Ct)
            .Returns(Result.Failure<RegisteredPlatformKey>(Error.Conflict("k.boom", "nope")));

        var result = await NewService().ProvisionHotWalletAsync(Chain.Tron, Address, SecretReference, cancellationToken: Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("k.boom");
        await _walletRegistrar.DidNotReceiveWithAnyArgs().RegisterPlatformWalletAsync(
            default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task A_failed_wallet_registration_propagates()
    {
        var derivedKeyId = Guid.NewGuid();
        _keyRegistrar.RegisterImportedKeyAsync(Chain.Tron, DerivationPurpose.Withdrawal, Address, SecretReference, Arg.Any<string?>(), Ct)
            .Returns(Result.Success(new RegisteredPlatformKey(derivedKeyId, Chain.Tron, Address, SecretReference)));
        _walletRegistrar.RegisterPlatformWalletAsync(derivedKeyId, Chain.Tron, Address, "HotWithdrawal", Arg.Any<string?>(), Ct)
            .Returns(Result.Failure<RegisteredPlatformWallet>(Error.Conflict("w.boom", "nope")));

        var result = await NewService().ProvisionHotWalletAsync(Chain.Tron, Address, SecretReference, cancellationToken: Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("w.boom");
    }
}
