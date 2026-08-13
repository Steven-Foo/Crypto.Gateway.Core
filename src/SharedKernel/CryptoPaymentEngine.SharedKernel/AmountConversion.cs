using System.Numerics;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// The single place a display decimal crosses into base-unit integers and back. This is an
/// <b>API/UI-edge concern only</b> (§14): base-unit <see cref="BigInteger"/> is the currency of Domain and
/// Application — never call this from inside a module's Domain/Application to "do the math in decimals".
/// It lives in SharedKernel, next to <see cref="MoneyLimits"/>, so both host edges (MerchantGateway and
/// OperationsApi) share one implementation instead of drifting copies. Inbound conversion refuses an amount
/// with finer precision than the asset supports, rather than silently truncating money.
/// </summary>
public static class AmountConversion
{
    /// <summary>
    /// Converts a positive display amount to base units. Fails if the amount is non-positive or has finer
    /// precision than <paramref name="decimals"/> allows (e.g. 1.2345678 for a 6-decimal asset).
    /// </summary>
    public static bool TryToBaseUnits(decimal display, int decimals, out BigInteger baseUnits)
    {
        baseUnits = BigInteger.Zero;
        if (display <= 0m)
            return false;

        var scaled = display * Pow10(decimals);
        if (scaled != decimal.Truncate(scaled))
            return false; // finer than the asset's precision — never truncate money

        baseUnits = new BigInteger(scaled);
        return baseUnits > BigInteger.Zero;
    }

    /// <summary>Converts base units to a display decimal for a response. Edge-only; amounts here are bounded.</summary>
    public static decimal ToDisplay(BigInteger baseUnits, int decimals) => (decimal)baseUnits / Pow10(decimals);

    private static decimal Pow10(int n)
    {
        var factor = 1m;
        for (var i = 0; i < n; i++)
            factor *= 10m;
        return factor;
    }
}
