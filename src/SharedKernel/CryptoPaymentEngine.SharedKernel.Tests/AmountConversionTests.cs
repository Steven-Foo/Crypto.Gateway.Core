using System.Globalization;
using System.Numerics;
using Shouldly;
using Xunit;

namespace CryptoPaymentEngine.SharedKernel.Tests;

// Money-edge conversion is §14-critical: it is the ONLY point display decimals cross into base units and
// back, and the rule is "refuse over-precision, never truncate". These are the first direct tests of that
// contract (previously it was duplicated, untested, in each host).
public class AmountConversionTests
{
    [Theory]
    [InlineData("1", 6, 1_000_000)]
    [InlineData("1.5", 6, 1_500_000)]
    [InlineData("0.000001", 6, 1)]                          // exact precision floor for a 6-dp asset
    [InlineData("1.500000", 6, 1_500_000)]                  // trailing zeros are not "extra precision"
    [InlineData("123.456789", 6, 123_456_789)]
    [InlineData("2", 18, 2_000_000_000_000_000_000)]        // wei scale
    [InlineData("0.5", 9, 500_000_000)]                     // lamports scale
    public void TryToBaseUnits_converts_valid_display_amounts(string display, int decimals, long expected)
    {
        var ok = AmountConversion.TryToBaseUnits(Parse(display), decimals, out var baseUnits);

        ok.ShouldBeTrue();
        baseUnits.ShouldBe(new BigInteger(expected));
    }

    [Theory]
    [InlineData("0", 6)]            // non-positive
    [InlineData("-1", 6)]           // negative — direction is never a sign at the edge
    [InlineData("1.2345678", 6)]    // 7 dp for a 6-dp asset — reject, do NOT drop the trailing 8
    [InlineData("0.0000001", 6)]    // would truncate to 0 — reject instead
    public void TryToBaseUnits_rejects_invalid_or_overprecise_amounts(string display, int decimals)
    {
        var ok = AmountConversion.TryToBaseUnits(Parse(display), decimals, out var baseUnits);

        ok.ShouldBeFalse();
        baseUnits.ShouldBe(BigInteger.Zero); // out-param stays zeroed on failure
    }

    [Theory]
    [InlineData(1_000_000, 6, "1")]
    [InlineData(1_500_000, 6, "1.5")]
    [InlineData(0, 6, "0")]
    [InlineData(1, 6, "0.000001")]
    public void ToDisplay_converts_base_units(long baseUnits, int decimals, string expected)
    {
        AmountConversion.ToDisplay(new BigInteger(baseUnits), decimals)
            .ShouldBe(Parse(expected));
    }

    [Theory]
    [InlineData("1234.567890", 6)]
    [InlineData("0.000001", 6)]
    [InlineData("999999.999999999999999999", 18)]
    public void Round_trip_display_to_base_and_back_is_lossless(string display, int decimals)
    {
        var original = Parse(display);

        AmountConversion.TryToBaseUnits(original, decimals, out var baseUnits).ShouldBeTrue();
        AmountConversion.ToDisplay(baseUnits, decimals).ShouldBe(original);
    }

    private static decimal Parse(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
}
