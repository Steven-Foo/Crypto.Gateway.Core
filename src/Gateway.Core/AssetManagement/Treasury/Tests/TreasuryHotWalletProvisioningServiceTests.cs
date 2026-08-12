using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Tests;

/// <summary>
/// The pool provisioner is idempotent and grow-only: it counts the existing <c>HotWithdrawal</c> wallets and
/// derives+registers just enough child wallets to reach the target size.
/// </summary>
public sealed class TreasuryHotWalletProvisioningServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IWalletDerivation _derivation = Substitute.For<IWalletDerivation>();
    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly IPlatformWalletRegistrar _walletRegistrar = Substitute.For<IPlatformWalletRegistrar>();

    private TreasuryHotWalletProvisioningService NewService() =>
        new(_derivation, _wallets, _walletRegistrar, NullLogger<TreasuryHotWalletProvisioningService>.Instance);

    private void ExistingHotWallets(int count)
    {
        var list = Enumerable.Range(0, count)
            .Select(i => new PlatformWallet(Guid.NewGuid(), Chain.Tron, $"TExisting{i}", "HotWithdrawal"))
            .ToList();
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PlatformWallet>>(list));
    }

    private void DeriveRegisterSucceeds()
    {
        var next = 0;
        _derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Arg.Any<CancellationToken>())
            .Returns(_ => Result.Success(new DerivedAddress(Guid.NewGuid(), Chain.Tron, $"TChild{next++}", next, "m/44'/195'/0'/0/0")));
        _walletRegistrar.RegisterPlatformWalletAsync(
                Arg.Any<Guid>(), Chain.Tron, Arg.Any<string>(), "HotWithdrawal", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result.Success(new RegisteredPlatformWallet(Guid.NewGuid(), Chain.Tron, (string)ci[2]!, "HotWithdrawal")));
    }

    [Fact]
    public async Task Derives_and_registers_up_to_the_target_from_empty()
    {
        ExistingHotWallets(0);
        DeriveRegisterSucceeds();

        (await NewService().EnsurePoolAsync(Chain.Tron, 3, Ct)).IsSuccess.ShouldBeTrue();

        await _derivation.Received(3).AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Arg.Any<CancellationToken>());
        await _walletRegistrar.Received(3).RegisterPlatformWalletAsync(
            Arg.Any<Guid>(), Chain.Tron, Arg.Any<string>(), "HotWithdrawal", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Is_idempotent_when_the_pool_is_already_at_target()
    {
        ExistingHotWallets(3);

        (await NewService().EnsurePoolAsync(Chain.Tron, 3, Ct)).IsSuccess.ShouldBeTrue();

        await _derivation.DidNotReceiveWithAnyArgs().AllocateNextAsync(default, default, default);
    }

    [Fact]
    public async Task Grows_the_pool_by_only_the_difference()
    {
        ExistingHotWallets(1);
        DeriveRegisterSucceeds();

        (await NewService().EnsurePoolAsync(Chain.Tron, 3, Ct)).IsSuccess.ShouldBeTrue();

        await _derivation.Received(2).AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_derivation_propagates_and_never_registers()
    {
        ExistingHotWallets(0);
        _derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Withdrawal, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DerivedAddress>(Error.NotFound("k.none", "no provisioner")));

        var result = await NewService().EnsurePoolAsync(Chain.Tron, 3, Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("k.none");
        await _walletRegistrar.DidNotReceiveWithAnyArgs().RegisterPlatformWalletAsync(
            default, default, default!, default!, default, default);
    }
}
