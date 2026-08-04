using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Treasury;
using CryptoPaymentEngine.SharedKernel;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Tests;

public sealed class TreasuryHotWalletProviderTests
{
    private const string Address = "TAueoxR1rwogpLDjYJzB7GGYYWgPbtajSs";
    private const string KeyReference = "tron-hot-wallet-0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ITreasuryHotWalletDirectory _treasury = Substitute.For<ITreasuryHotWalletDirectory>();

    [Fact]
    public async Task Maps_a_registered_hot_wallet_to_the_withdrawal_hot_wallet_shape()
    {
        _treasury.GetHotWalletAsync(Chain.Tron, Ct)
            .Returns(Result.Success(new TreasuryHotWallet(Chain.Tron, Address, KeyReference)));

        var hotWallet = await new TreasuryHotWalletProvider(_treasury).ForAsync(Chain.Tron, Ct);

        hotWallet.Address.ShouldBe(Address);
        hotWallet.KeyReference.ShouldBe(KeyReference);
    }

    [Fact]
    public async Task Throws_when_no_hot_wallet_is_registered_so_a_withdrawal_is_never_built_without_a_source()
    {
        _treasury.GetHotWalletAsync(Chain.Tron, Ct)
            .Returns(Result.Failure<TreasuryHotWallet>(Error.NotFound("treasury.none", "no hot wallet")));

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await new TreasuryHotWalletProvider(_treasury).ForAsync(Chain.Tron, Ct));
    }
}
