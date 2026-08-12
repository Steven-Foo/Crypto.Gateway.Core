using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;

/// <summary>
/// Per-(chain, asset) sweep policy, all base units (§14). <see cref="MinSweepAmount"/> is the economic
/// threshold: a deposit address is swept only once its on-chain balance reaches this, so we never spend more
/// gas concentrating dust than the dust is worth. <see cref="Confirmations"/> is the on-chain depth a broadcast
/// sweep must reach before it is treated as final.
/// </summary>
public sealed record SweepPolicy(BigInteger MinSweepAmount, int Confirmations)
{
    /// <summary>A balance at or above the threshold is worth sweeping.</summary>
    public bool ShouldSweep(BigInteger balance) => balance > BigInteger.Zero && balance >= MinSweepAmount;
}
