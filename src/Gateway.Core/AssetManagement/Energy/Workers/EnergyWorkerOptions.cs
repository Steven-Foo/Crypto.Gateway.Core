using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;

public sealed class EnergyWorkerOptions
{
    /// <summary>Chains to monitor. Energy is TRON-specific, so this is <c>[Chain.Tron]</c> today.</summary>
    public IReadOnlyList<Chain> Chains { get; init; } = [];

    /// <summary>How often to sample every platform wallet's resources.</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromMinutes(1);
}
