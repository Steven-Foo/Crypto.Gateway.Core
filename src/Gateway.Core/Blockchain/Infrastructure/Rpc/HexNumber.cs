using System.Globalization;
using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;

/// <summary>
/// Parsing for the <c>0x</c>-prefixed hex quantities Ethereum-style JSON-RPC returns. Amounts become
/// unsigned <see cref="BigInteger"/> base units with no precision loss (§14) — never <c>double</c>.
/// </summary>
public static class HexNumber
{
    /// <summary>Parses a <c>0x</c> hex quantity as an unsigned integer. Empty/"0x" ⇒ 0.</summary>
    public static BigInteger ToBigInteger(string hex)
    {
        var digits = Strip(hex);
        if (digits.Length == 0)
            return BigInteger.Zero;

        // Prepend '0' so the leading hex digit is never read as a sign bit — keeps the value unsigned.
        Span<char> buffer = stackalloc char[digits.Length + 1];
        buffer[0] = '0';
        digits.CopyTo(buffer[1..]);
        return BigInteger.Parse(buffer, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static long ToInt64(string hex)
    {
        var digits = Strip(hex);
        return digits.Length == 0 ? 0L : long.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static ReadOnlySpan<char> Strip(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.StartsWith("0x") || span.StartsWith("0X"))
            span = span[2..];
        return span;
    }
}
