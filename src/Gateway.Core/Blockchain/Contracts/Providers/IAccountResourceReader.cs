using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>
/// A point-in-time view of an address's TRON resources (from <c>getaccountresource</c> + account balance).
/// All amounts are exact base units — energy/bandwidth in their own units, TRX in sun. This is
/// OBSERVATION only: it proves what the chain currently reports, it is never a balance the ledger trusts (§8).
/// </summary>
public sealed record AccountResourceSnapshot(
    Chain Chain,
    string Address,
    BigInteger EnergyLimit,
    BigInteger EnergyUsed,
    BigInteger BandwidthLimit,
    BigInteger BandwidthUsed,
    BigInteger FrozenTrxForEnergy,
    BigInteger FrozenTrxForBandwidth,
    BigInteger DelegatedEnergyOut,
    BigInteger DelegatedEnergyIn,
    BigInteger AvailableTrxBalance,
    DateTimeOffset ObservedAt)
{
    /// <summary>Energy currently usable = limit − used. Never negative in practice; clamped for safety.</summary>
    public BigInteger EnergyAvailable => BigInteger.Max(BigInteger.Zero, EnergyLimit - EnergyUsed);

    public BigInteger BandwidthAvailable => BigInteger.Max(BigInteger.Zero, BandwidthLimit - BandwidthUsed);

    /// <summary>The whole network's daily energy supply (TRON <c>TotalEnergyLimit</c>). Zero when unknown.</summary>
    public BigInteger NetworkTotalEnergyLimit { get; init; }

    /// <summary>The whole network's TRX staked for energy, in whole TRX (TRON <c>TotalEnergyWeight</c>). Zero
    /// when unknown. Energy per staked TRX = <see cref="NetworkTotalEnergyLimit"/> / this.</summary>
    public BigInteger NetworkTotalEnergyWeight { get; init; }
}

/// <summary>
/// A read-only chain capability (§8): fetch an address's current resource standing. Tiny and read-only by
/// design — a resource monitor gets no ability to freeze, delegate, or sign (§10). Implementations are
/// chain-specific adapters (in-memory for dev/test; TRON <c>getaccountresource</c> for staging/prod),
/// selected purely by DI. Energy is TRON-specific, so today only a TRON adapter exists.
/// </summary>
public interface IAccountResourceReader
{
    Task<AccountResourceSnapshot> GetAsync(Chain chain, string address, CancellationToken cancellationToken = default);
}
