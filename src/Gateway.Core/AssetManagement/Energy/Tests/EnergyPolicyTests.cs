using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.SharedKernel;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Tests;

public sealed class EnergyPolicyTests
{
    private static EnergyPolicy Policy(BigInteger minimum, BigInteger target) =>
        EnergyPolicy.Create(Chain.Tron, "HotWithdrawal", minimum, target, target, target, false, false).Value;

    [Fact]
    public void Create_requires_a_wallet_type()
    {
        var result = EnergyPolicy.Create(Chain.Tron, "  ", 1, 2, 2, 2, false, false);
        result.Error!.Code.ShouldBe(EnergyErrors.WalletTypeRequired.Code);
    }

    [Fact]
    public void Create_rejects_negative_thresholds()
    {
        var result = EnergyPolicy.Create(Chain.Tron, "HotWithdrawal", -1, 2, 2, 2, false, false);
        result.Error!.Code.ShouldBe(EnergyErrors.NegativeThreshold.Code);
    }

    [Fact]
    public void Create_rejects_target_below_minimum()
    {
        var result = EnergyPolicy.Create(Chain.Tron, "HotWithdrawal", 5, 4, 4, 4, false, false);
        result.Error!.Code.ShouldBe(EnergyErrors.TargetBelowMinimum.Code);
    }

    [Fact]
    public void Create_succeeds_and_is_enabled()
    {
        var policy = Policy(1_000_000, 5_000_000);
        policy.IsEnabled.ShouldBeTrue();
        policy.Chain.ShouldBe(Chain.Tron);
        policy.WalletType.ShouldBe("HotWithdrawal");
    }

    [Theory]
    [InlineData("999999", ResourceHealth.Critical)]   // below minimum
    [InlineData("1000000", ResourceHealth.Low)]       // exactly minimum → not yet Critical
    [InlineData("3000000", ResourceHealth.Low)]       // between minimum and target
    [InlineData("4999999", ResourceHealth.Low)]       // just below target
    [InlineData("5000000", ResourceHealth.Healthy)]   // exactly target
    [InlineData("9000000", ResourceHealth.Healthy)]   // above target
    public void Classify_maps_available_energy_to_health(string available, ResourceHealth expected)
    {
        var policy = Policy(1_000_000, 5_000_000);
        policy.Classify(BigInteger.Parse(available)).ShouldBe(expected);
    }

    [Fact]
    public void Update_applies_new_thresholds()
    {
        var policy = Policy(1_000_000, 5_000_000);

        var result = policy.Update(2_000_000, 8_000_000, 3_000_000, 1_000_000, true, true, DateTimeOffset.UtcNow);

        result.IsSuccess.ShouldBeTrue();
        policy.MinimumEnergy.ShouldBe(2_000_000);
        policy.TargetEnergy.ShouldBe(8_000_000);
        policy.EnableAutoStake.ShouldBeTrue();
        policy.Classify(1_500_000).ShouldBe(ResourceHealth.Critical);
    }

    [Fact]
    public void Update_rejects_target_below_minimum()
    {
        var policy = Policy(1_000_000, 5_000_000);
        var result = policy.Update(5, 4, 4, 4, false, false, DateTimeOffset.UtcNow);
        result.Error!.Code.ShouldBe(EnergyErrors.TargetBelowMinimum.Code);
    }
}
