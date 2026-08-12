using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

/// <summary>
/// Policy-driven auto-stake: the staking wallet is topped up only when its energy is at/below the policy's
/// stake threshold AND auto-stake is enabled — never otherwise, and never twice at once.
/// </summary>
public sealed class StakingServiceTests
{
    private const string StakingAddress = "TStaker";
    private static readonly Guid StakingWalletId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly InMemoryAccountResourceReader _resources = new(TimeProvider.System);
    private readonly IEnergyPolicyRepository _policies = Substitute.For<IEnergyPolicyRepository>();
    private readonly IEnergyOperationRepository _operations = Substitute.For<IEnergyOperationRepository>();
    private readonly EnergyOperationOptions _options = new();

    public StakingServiceTests()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns([new PlatformWallet(StakingWalletId, Chain.Tron, StakingAddress, "Energy")]);
        _operations.HasInFlightStakeAsync(StakingWalletId, Arg.Any<CancellationToken>()).Returns(false);
        _operations.TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static EnergyPolicy Policy(bool autoStake) =>
        EnergyPolicy.Create(Chain.Tron, StakingWalletLocator.EnergyWalletType,
            minimumEnergy: 0, targetEnergy: 0, stakeThreshold: 1_000_000, rentalThreshold: 0,
            enableAutoStake: autoStake, enableAutoRent: false).Value;

    private StakingService Service => new(
        new StakingWalletLocator(_wallets), _resources, _policies, _operations, _options, TimeProvider.System,
        NullLogger<StakingService>.Instance);

    [Fact]
    public async Task A_wallet_at_or_below_the_stake_threshold_queues_a_stake()
    {
        _policies.FindAsync(Chain.Tron, StakingWalletLocator.EnergyWalletType, Arg.Any<CancellationToken>()).Returns(Policy(autoStake: true));
        _resources.SetEnergyAvailable(Chain.Tron, StakingAddress, 500_000); // ≤ threshold 1_000_000

        EnergyOperation? created = null;
        _operations.TryAddAsync(Arg.Do<EnergyOperation>(o => created = o), Arg.Any<CancellationToken>()).Returns(true);

        (await Service.ReplenishAsync(Chain.Tron, Ct)).ShouldBe(1);
        created.ShouldNotBeNull();
        created.Kind.ShouldBe(EnergyOperationKind.Stake);
        created.AmountSun.ShouldBe(_options.StakeIncrementTrxSun);
    }

    [Fact]
    public async Task A_wallet_above_the_threshold_is_left_alone()
    {
        _policies.FindAsync(Chain.Tron, StakingWalletLocator.EnergyWalletType, Arg.Any<CancellationToken>()).Returns(Policy(autoStake: true));
        _resources.SetEnergyAvailable(Chain.Tron, StakingAddress, 5_000_000); // > threshold

        (await Service.ReplenishAsync(Chain.Tron, Ct)).ShouldBe(0);
        await _operations.DidNotReceive().TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auto_stake_disabled_never_stakes_even_when_low()
    {
        _policies.FindAsync(Chain.Tron, StakingWalletLocator.EnergyWalletType, Arg.Any<CancellationToken>()).Returns(Policy(autoStake: false));
        _resources.SetEnergyAvailable(Chain.Tron, StakingAddress, 0);

        (await Service.ReplenishAsync(Chain.Tron, Ct)).ShouldBe(0);
        await _operations.DidNotReceive().TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_staking_wallet_means_nothing_to_replenish()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns([]);
        _policies.FindAsync(Chain.Tron, StakingWalletLocator.EnergyWalletType, Arg.Any<CancellationToken>()).Returns(Policy(autoStake: true));

        (await Service.ReplenishAsync(Chain.Tron, Ct)).ShouldBe(0);
        await _operations.DidNotReceive().TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>());
    }
}
