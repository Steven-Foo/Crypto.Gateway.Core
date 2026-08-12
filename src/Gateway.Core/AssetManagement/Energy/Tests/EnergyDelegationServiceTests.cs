using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
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
/// The Sweep-facing coordination seam: ready a deposit address's energy, delegating from the staking wallet
/// when short. Proves it delegates only when needed, never without a staking source, and never duplicates a
/// delegation already in flight.
/// </summary>
public sealed class EnergyDelegationServiceTests
{
    private const string StakingAddress = "TStaker";
    private const string Deposit = "TDeposit";
    private static readonly Guid StakingWalletId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly InMemoryAccountResourceReader _resources = new(TimeProvider.System);
    private readonly IEnergyOperationRepository _operations = Substitute.For<IEnergyOperationRepository>();
    private readonly EnergyOperationOptions _options = new(); // RequiredEnergyPerTransfer = 131_000

    public EnergyDelegationServiceTests()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>())
            .Returns([new PlatformWallet(StakingWalletId, Chain.Tron, StakingAddress, "Energy")]);
        _operations.HasInFlightDelegateAsync(Chain.Tron, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _operations.TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private EnergyDelegationService Service => new(
        new StakingWalletLocator(_wallets), _resources, _operations, _options, TimeProvider.System,
        NullLogger<EnergyDelegationService>.Instance);

    [Fact]
    public async Task An_address_with_enough_energy_is_ready_and_no_delegation_is_created()
    {
        _resources.SetEnergyAvailable(Chain.Tron, Deposit, 200_000); // > 131_000 required

        (await Service.EnsureEnergyForTransferAsync(Chain.Tron, Deposit, Ct)).ShouldBe(EnergyReadiness.Ready);
        await _operations.DidNotReceive().TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_short_address_gets_a_delegation_from_the_staking_wallet_and_reads_provisioning()
    {
        _resources.SetEnergyAvailable(Chain.Tron, Deposit, 0);

        EnergyOperation? created = null;
        _operations.TryAddAsync(Arg.Do<EnergyOperation>(o => created = o), Arg.Any<CancellationToken>()).Returns(true);

        (await Service.EnsureEnergyForTransferAsync(Chain.Tron, Deposit, Ct)).ShouldBe(EnergyReadiness.Provisioning);

        created.ShouldNotBeNull();
        created.Kind.ShouldBe(EnergyOperationKind.Delegate);
        created.OwnerAddress.ShouldBe(StakingAddress);
        created.TargetAddress.ShouldBe(Deposit);
        created.AmountSun.ShouldBe(_options.DelegateTrxSun);
    }

    [Fact]
    public async Task Without_a_staking_wallet_a_short_address_is_unavailable_never_burning_trx()
    {
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns([]);
        _resources.SetEnergyAvailable(Chain.Tron, Deposit, 0);

        (await Service.EnsureEnergyForTransferAsync(Chain.Tron, Deposit, Ct)).ShouldBe(EnergyReadiness.Unavailable);
        await _operations.DidNotReceive().TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_delegation_already_in_flight_is_not_duplicated()
    {
        _resources.SetEnergyAvailable(Chain.Tron, Deposit, 0);
        _operations.HasInFlightDelegateAsync(Chain.Tron, Deposit, Arg.Any<CancellationToken>()).Returns(true);

        (await Service.EnsureEnergyForTransferAsync(Chain.Tron, Deposit, Ct)).ShouldBe(EnergyReadiness.Provisioning);
        await _operations.DidNotReceive().TryAddAsync(Arg.Any<EnergyOperation>(), Arg.Any<CancellationToken>());
    }
}
