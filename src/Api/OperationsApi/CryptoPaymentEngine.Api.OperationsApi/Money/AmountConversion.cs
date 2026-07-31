using System.Numerics;

namespace CryptoPaymentEngine.Api.OperationsApi.Money;

/// <summary>
/// The single place a base-unit integer crosses into a display decimal for this host — only ever at the API
/// edge (§14). Ops is read-only for money (never posts a ledger entry itself), so only the outbound
/// conversion exists here — no inbound base-unit parsing from a display amount.
/// </summary>
public static class AmountConversion
{
    public static decimal ToDisplay(BigInteger baseUnits, int decimals) => (decimal)baseUnits / Pow10(decimals);

    private static decimal Pow10(int n)
    {
        var factor = 1m;
        for (var i = 0; i < n; i++)
            factor *= 10m;
        return factor;
    }
}
