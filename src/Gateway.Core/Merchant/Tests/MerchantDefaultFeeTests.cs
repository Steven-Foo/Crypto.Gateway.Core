using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>The platform-default fee holder: config → resolved <see cref="FeeSchedule"/>, built once. Zero or
/// invalid config must degrade to <see cref="FeeSchedule.None"/> (no default) rather than crash the module.</summary>
public sealed class MerchantDefaultFeeTests
{
    private static MerchantDefaultFee Build(int depositBps, int withdrawalBps) =>
        new(Options.Create(new MerchantDefaultFeeOptions { DepositFeeBps = depositBps, WithdrawalFeeBps = withdrawalBps }),
            NullLogger<MerchantDefaultFee>.Instance);

    [Fact]
    public void Zero_bps_on_both_means_no_default()
    {
        Build(0, 0).Schedule.ShouldBe(FeeSchedule.None);
    }

    [Fact]
    public void Configured_bps_build_a_percentage_only_default_schedule()
    {
        var schedule = Build(100, 50).Schedule;
        schedule.DepositFeeBps.ShouldBe(100);
        schedule.WithdrawalFeeBps.ShouldBe(50);
        schedule.DepositFeeFixed.ShouldBe(BigInteger.Zero); // percentage-only — no platform-wide flat default
        schedule.WithdrawalFee.ShouldBe(BigInteger.Zero);
    }

    [Fact]
    public void Invalid_bps_degrades_to_no_default_rather_than_throwing()
    {
        // 10000 bps deposit = 100% is rejected by FeeSchedule.Create (gross-up unsolvable) ⇒ None, not a crash.
        Build(10_000, 0).Schedule.ShouldBe(FeeSchedule.None);
    }
}
