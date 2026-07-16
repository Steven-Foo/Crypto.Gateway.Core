namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;

/// <summary>
/// How a wallet's energy stands against its policy. Phase 5a only observes and alerts on this; the
/// automated response (delegate / stake / rent) arrives in 5b.
/// </summary>
public enum ResourceHealth
{
    /// <summary>At or above target — nothing to do.</summary>
    Healthy = 1,

    /// <summary>Below target but at or above minimum — replenish soon (5b), alert now.</summary>
    Low = 2,

    /// <summary>Below minimum — withdrawals/sweeps may start burning TRX; act first (5b), alert now.</summary>
    Critical = 3,
}
