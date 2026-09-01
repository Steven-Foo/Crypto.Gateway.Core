using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Tests;

/// <summary>
/// The fee arithmetic — the money-critical core of the hybrid pricing model. Fees are
/// <c>fixed + ⌊amount·bps/10000⌋</c>, and the payer-on-top gross-up is the exact inverse under integer
/// flooring: the merchant nets at least their target, never over-asking the payer by even one base unit.
/// </summary>
public sealed class FeeScheduleTests
{
    private static FeeSchedule Schedule(BigInteger depFixed, int depBps, BigInteger wdFixed, int wdBps) =>
        FeeSchedule.Create(depFixed, depBps, wdFixed, wdBps).Value;

    [Fact]
    public void None_charges_nothing()
    {
        FeeSchedule.None.QuoteDepositFee(1_000_000).ShouldBe(BigInteger.Zero);
        FeeSchedule.None.QuoteWithdrawalFee(1_000_000).ShouldBe(BigInteger.Zero);
    }

    [Theory]
    // fixed only, bps only, combined, and the flooring of a fractional basis-point charge.
    [InlineData("1000", 0, "1000000", "1000")]        // fixed 1000
    [InlineData("0", 50, "1000000", "5000")]          // 0.5% of 1e6
    [InlineData("1000", 50, "1000000", "6000")]       // fixed + 0.5%
    [InlineData("0", 33, "1000000", "3300")]          // 0.33% exact
    [InlineData("0", 1, "999", "0")]                  // 0.01% of 999 = 0.0999 → floors to 0
    public void QuoteDepositFee_is_fixed_plus_floored_percentage(string depFixed, int depBps, string amount, string expected)
    {
        Schedule(BigInteger.Parse(depFixed), depBps, 0, 0)
            .QuoteDepositFee(BigInteger.Parse(amount))
            .ShouldBe(BigInteger.Parse(expected));
    }

    [Theory]
    [InlineData("100000", 0, "3000000", "100000")]    // flat 0.1 USDT
    [InlineData("0", 100, "3000000", "30000")]        // 1%
    [InlineData("100000", 100, "3000000", "130000")]  // flat + 1%
    public void QuoteWithdrawalFee_is_fixed_plus_floored_percentage(string wdFixed, int wdBps, string amount, string expected)
    {
        Schedule(0, 0, BigInteger.Parse(wdFixed), wdBps)
            .QuoteWithdrawalFee(BigInteger.Parse(amount))
            .ShouldBe(BigInteger.Parse(expected));
    }

    [Fact]
    public void Create_rejects_a_negative_fixed_fee() =>
        FeeSchedule.Create(BigInteger.MinusOne, 0, 0, 0).Error!.Code.ShouldBe(MerchantErrors.AmountNegative.Code);

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Create_rejects_out_of_range_deposit_bps(int bps) =>
        FeeSchedule.Create(0, bps, 0, 0).Error!.Code.ShouldBe(MerchantErrors.FeeBpsInvalid.Code);

    /// <summary>
    /// 10 000 bps = 100% is now ACCEPTED for a deposit, as it always was for a withdrawal. It used to be
    /// rejected because the payer-on-top gross-up became unsolvable at 100% — with the fee deducted from what
    /// arrives instead, the arithmetic is fine (the merchant is simply credited nothing). Absurd, not
    /// impossible; the platform decides what it charges, and the domain only guards what it cannot compute.
    /// </summary>
    [Fact]
    public void Create_accepts_a_100_percent_deposit_fee_now_that_it_is_deducted_not_grossed_up()
    {
        var schedule = FeeSchedule.Create(0, FeeSchedule.MaxBps, 0, 0);

        schedule.IsSuccess.ShouldBeTrue();
        schedule.Value.QuoteDepositFee(1_000_000).ShouldBe(new BigInteger(1_000_000));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Create_rejects_out_of_range_withdrawal_bps(int bps) =>
        FeeSchedule.Create(0, 0, 0, bps).Error!.Code.ShouldBe(MerchantErrors.FeeBpsInvalid.Code);

    [Fact]
    public void Create_allows_a_100_percent_withdrawal_bps_but_not_deposit()
    {
        FeeSchedule.Create(0, 0, 0, 10_000).IsSuccess.ShouldBeTrue();     // withdrawal 100% is legal (deducted)
        FeeSchedule.Create(0, 9_999, 0, 0).IsSuccess.ShouldBeTrue();      // just under 100% deposit is the ceiling
    }
}
