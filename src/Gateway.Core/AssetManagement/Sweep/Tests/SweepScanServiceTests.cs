using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Tests;

/// <summary>
/// The scanner turns "a deposit address holds enough to be worth sweeping" into a Pending sweep into the cold
/// treasury. Money-relevant: it sweeps only at/above the threshold (never dust), never when one is already in
/// flight, and never at all without a registered cold-treasury destination.
/// </summary>
public sealed class SweepScanServiceTests
{
    private const string ColdWallet = "TColdTreasury";
    private const string Deposit1 = "TDeposit1";
    private const string Deposit2 = "TDeposit2";
    private static readonly Guid Usdt = Guid.CreateVersion7();
    private static readonly Guid Wallet1 = Guid.CreateVersion7();
    private static readonly Guid Wallet2 = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAssetCatalog _assets = Substitute.For<IAssetCatalog>();
    private readonly IWalletDirectory _wallets = Substitute.For<IWalletDirectory>();
    private readonly InMemoryBalanceReader _balances = new();
    private readonly ITreasuryColdWalletDirectory _coldWallets = Substitute.For<ITreasuryColdWalletDirectory>();
    private readonly ISweepPolicyProvider _policies = Substitute.For<ISweepPolicyProvider>();
    private readonly ISweepRepository _repository = Substitute.For<ISweepRepository>();

    public SweepScanServiceTests()
    {
        _assets.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns([new AssetDto(Usdt, Chain.Tron, "USDT", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", 6, IsNative: false)]);
        _wallets.ListReceivingDepositAddressesAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns([new ReceivingDepositAddress(Wallet1, Deposit1), new ReceivingDepositAddress(Wallet2, Deposit2)]);
        _coldWallets.GetAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ColdTreasuryWallet(Chain.Tron, ColdWallet)));
        _policies.For(Chain.Tron).Returns(new SweepPolicy(1_000_000, 19)); // threshold 1 USDT
        _repository.HasInFlightAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _repository.TryAddAsync(Arg.Any<Domain.Sweep>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private SweepScanService Service => new(
        _assets, _wallets, _balances, _coldWallets, _policies, _repository, TimeProvider.System,
        NullLogger<SweepScanService>.Instance);

    [Fact]
    public async Task Only_addresses_at_or_above_the_threshold_are_swept_and_the_full_balance_goes_to_the_cold_treasury()
    {
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000); // >= threshold
        _balances.Set(Chain.Tron, Deposit2, Usdt, 500_000);   // dust, below threshold

        Domain.Sweep? created = null;
        _repository.TryAddAsync(Arg.Do<Domain.Sweep>(s => created = s), Arg.Any<CancellationToken>()).Returns(true);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(1);
        created.ShouldNotBeNull();
        created.FromAddress.ShouldBe(Deposit1);
        created.ToAddress.ShouldBe(ColdWallet);
        created.Amount.ShouldBe(new BigInteger(5_000_000)); // the full balance is swept
        created.WalletId.ShouldBe(Wallet1);
    }

    [Fact]
    public async Task An_address_with_a_sweep_already_in_flight_is_skipped()
    {
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);
        _balances.Set(Chain.Tron, Deposit2, Usdt, 5_000_000);
        _repository.HasInFlightAsync(Wallet1, Usdt, Arg.Any<CancellationToken>()).Returns(true); // one already moving

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(1); // only Deposit2
        await _repository.DidNotReceive().TryAddAsync(
            Arg.Is<Domain.Sweep>(s => s.FromAddress == Deposit1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_is_swept_when_no_cold_treasury_destination_is_registered()
    {
        _coldWallets.GetAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ColdTreasuryWallet>(Error.NotFound("treasury.none", "no cold treasury")));
        _balances.Set(Chain.Tron, Deposit1, Usdt, 5_000_000);

        var count = await Service.ScanAsync(Chain.Tron, Ct);

        count.ShouldBe(0);
        await _repository.DidNotReceive().TryAddAsync(Arg.Any<Domain.Sweep>(), Arg.Any<CancellationToken>());
    }
}
