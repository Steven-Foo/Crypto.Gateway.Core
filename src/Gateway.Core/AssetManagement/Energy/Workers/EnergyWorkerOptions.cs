using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Workers;

public sealed class EnergyWorkerOptions
{
    /// <summary>Chains to monitor. Energy is TRON-specific, so this is <c>[Chain.Tron]</c> today.</summary>
    public IReadOnlyList<Chain> Chains { get; init; } = [];

    /// <summary>How often to sample every platform wallet's resources (5a).</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>5b: how often to check the staking wallet and queue an auto-stake top-up if it's low.</summary>
    public TimeSpan StakeReplenishInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>5b: how often to build/sign/broadcast Pending stake/delegate operations.</summary>
    public TimeSpan OperationProcessInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>5b: how often to confirm broadcast stake/delegate operations.</summary>
    public TimeSpan OperationConfirmationInterval { get; init; } = TimeSpan.FromSeconds(15);
}
