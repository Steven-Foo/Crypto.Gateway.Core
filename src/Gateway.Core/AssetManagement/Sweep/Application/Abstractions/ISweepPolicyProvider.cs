using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;

/// <summary>Resolves the sweep policy for a chain. A per-asset (rather than per-chain) policy is a future
/// refinement, mirroring Withdrawal.</summary>
public interface ISweepPolicyProvider
{
    /// <summary>
    /// Asynchronous because the effective policy is a stored row an operator can change while the platform
    /// runs, layered over the deployed configuration — not a value fixed at startup.
    /// </summary>
    Task<SweepPolicy> ForAsync(Chain chain, CancellationToken cancellationToken = default);
}

/// <summary>
/// The deployed defaults a chain's stored settings start from and keep tracking until staff save their own.
/// Configuration stays the floor: a fresh environment must never boot with no sweep policy at all, and a
/// chain absent here is never swept.
/// </summary>
public interface ISweepConfigurationDefaults
{
    IReadOnlyDictionary<Chain, SweepConfigurationDefault> Configured { get; }
}

/// <param name="ScanIntervalMinutes">How often a scan pass runs, until staff change it.</param>
public sealed record SweepConfigurationDefault(
    BigInteger MinSweepAmount, int Confirmations, int ScanIntervalMinutes);
