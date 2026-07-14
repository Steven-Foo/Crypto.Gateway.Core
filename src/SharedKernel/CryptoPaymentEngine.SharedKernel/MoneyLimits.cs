using System.Numerics;

namespace CryptoPaymentEngine.SharedKernel;

/// <summary>
/// The platform-wide envelope for base-unit amounts. Lives here (not in Infrastructure) because
/// Domain must be able to reject an out-of-range amount, and Domain may not reference
/// Infrastructure (§4.4). The 38-digit bound is where storage and domain agree: it comfortably
/// covers every real asset (total ETH supply in wei is 27 digits).
///
/// Out-of-range amounts are rejected, never truncated (§14).
/// </summary>
public static class MoneyLimits
{
    public const int MaxPrecision = 38;

    /// <summary>10^38 - 1.</summary>
    public static readonly BigInteger MaxValue = BigInteger.Pow(10, MaxPrecision) - BigInteger.One;

    public static bool IsStorable(BigInteger value) => value >= BigInteger.Zero && value <= MaxValue;

    /// <summary>
    /// Base units are unsigned magnitudes; direction is expressed by which column holds the value
    /// (e.g. ledger Debit vs Credit), never by a sign.
    /// </summary>
    public static BigInteger EnsureStorable(BigInteger value, string parameterName)
    {
        if (value < BigInteger.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, "Base-unit amounts are unsigned; use the Debit/Credit column to express direction.");
        }

        if (value > MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Amount has {value.ToString().Length} digits and exceeds the {MaxPrecision}-digit storage limit. " +
                "Rejecting rather than truncating.");
        }

        return value;
    }
}
