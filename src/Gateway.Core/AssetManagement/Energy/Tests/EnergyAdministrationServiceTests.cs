using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

public sealed class EnergyAdministrationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPlatformWalletDirectory _wallets = Substitute.For<IPlatformWalletDirectory>();
    private readonly IWalletDerivation _derivation = Substitute.For<IWalletDerivation>();
    private readonly IPlatformWalletRegistrar _registrar = Substitute.For<IPlatformWalletRegistrar>();
    private readonly IEnergyPolicyRepository _policies = Substitute.For<IEnergyPolicyRepository>();
    private readonly IEnergyOperationRepository _operations = Substitute.For<IEnergyOperationRepository>();
    private readonly IEnergyProvisioningLock _lock = Substitute.For<IEnergyProvisioningLock>();

    public EnergyAdministrationServiceTests()
    {
        _lock.AcquireAsync(Arg.Any<Chain>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        Existing();
    }

    private EnergyAdministrationService Service => new(
        new StakingWalletLocator(_wallets), _wallets, _derivation, _registrar, _policies, _operations, _lock,
        TimeProvider.System, NullLogger<EnergyAdministrationService>.Instance);

    private void Existing(params string[] energyAddresses) =>
        _wallets.GetPlatformWalletsAsync(Chain.Tron, Arg.Any<CancellationToken>()).Returns(
            energyAddresses.Select(a => new PlatformWallet(Guid.NewGuid(), Chain.Tron, a, "Energy"))
                .Append(new PlatformWallet(Guid.NewGuid(), Chain.Tron, "THot", "HotWithdrawal"))
                .ToList());

    [Fact]
    public async Task Creates_one_energy_child_and_registers_it_as_the_staking_wallet()
    {
        var keyId = Guid.NewGuid();
        _derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Energy, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DerivedAddress(keyId, Chain.Tron, "TEnergy0", 0, "m/44'/195'/0'/0/0")));
        _registrar.RegisterPlatformWalletAsync(keyId, Chain.Tron, "TEnergy0", "Energy", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RegisteredPlatformWallet(Guid.NewGuid(), Chain.Tron, "TEnergy0", "Energy")));

        var result = await Service.ProvisionStakingWalletAsync(Chain.Tron, Ct);

        result.Value.Created.ShouldBeTrue();
        result.Value.Address.ShouldBe("TEnergy0");
        await _lock.Received(1).AcquireAsync(Chain.Tron, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Asking_again_returns_the_existing_wallet_and_derives_nothing()
    {
        Existing("TEnergyExisting");

        var result = await Service.ProvisionStakingWalletAsync(Chain.Tron, Ct);

        result.Value.Created.ShouldBeFalse();
        result.Value.Address.ShouldBe("TEnergyExisting");
        await _derivation.DidNotReceiveWithAnyArgs().AllocateNextAsync(default, default, Ct);
    }

    [Fact]
    public async Task Two_staking_wallets_are_reported_and_never_made_three()
    {
        Existing("TEnergyA", "TEnergyB");

        var result = await Service.ProvisionStakingWalletAsync(Chain.Tron, Ct);

        result.Error!.Code.ShouldBe(EnergyErrors.StakingWalletAmbiguous.Code);
        await _derivation.DidNotReceiveWithAnyArgs().AllocateNextAsync(default, default, Ct);
    }

    [Fact]
    public async Task A_missing_energy_key_surfaces_and_registers_nothing()
    {
        _derivation.AllocateNextAsync(Chain.Tron, DerivationPurpose.Energy, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DerivedAddress>(Error.Validation("keymgmt.energy_key_not_configured", "no key")));

        var result = await Service.ProvisionStakingWalletAsync(Chain.Tron, Ct);

        result.Error!.Code.ShouldBe("keymgmt.energy_key_not_configured");
        await _registrar.DidNotReceiveWithAnyArgs().RegisterPlatformWalletAsync(default, default, default!, default!, default, Ct);
    }

    [Fact]
    public async Task Saving_rules_creates_the_policy_first_time_and_updates_it_after()
    {
        EnergyPolicy? stored = null;
        _policies.FindAsync(Chain.Tron, "Energy", Arg.Any<CancellationToken>()).Returns(_ => stored);
        _policies.When(p => p.Add(Arg.Any<EnergyPolicy>())).Do(ci => stored = ci.Arg<EnergyPolicy>());

        var first = await Service.SetPolicyAsync(new SetStakingPolicyCommand(Chain.Tron, 100_000, 500_000, 200_000, true), Ct);
        first.IsSuccess.ShouldBeTrue();
        stored.ShouldNotBeNull();
        stored.EnableAutoRent.ShouldBeFalse(); // rental is not built — never switched on from here

        var second = await Service.SetPolicyAsync(new SetStakingPolicyCommand(Chain.Tron, 100_000, 900_000, 300_000, false), Ct);
        second.Value.TargetEnergy.ShouldBe(new BigInteger(900_000));
        second.Value.EnableAutoStake.ShouldBeFalse();
        _policies.Received(1).Add(Arg.Any<EnergyPolicy>());
        await _policies.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_target_below_the_minimum_is_refused_and_nothing_is_saved()
    {
        var result = await Service.SetPolicyAsync(new SetStakingPolicyCommand(Chain.Tron, 500_000, 100_000, 0, true), Ct);

        result.Error!.Code.ShouldBe(EnergyErrors.TargetBelowMinimum.Code);
        await _policies.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
