namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Infrastructure;

/// <summary>
/// DEV/TESTNET-tier config for seeding the platform staking (energy) wallet(s). Bound from <c>Energy</c>.
/// The signing key lives in <c>KeyManagement:DevSecrets</c> under <see cref="StakingWalletSeed.SecretReference"/>
/// (a throwaway testnet key — never a real key, §10); this section carries only the address + thresholds.
/// </summary>
public sealed class EnergyStakingDevOptions
{
    public const string SectionName = "Energy";

    public List<StakingWalletSeed> DevStakingWallets { get; init; } = [];
}

public sealed class StakingWalletSeed
{
    public string Chain { get; init; } = null!;

    /// <summary>The staking wallet's address. It must be funded with (testnet) TRX to freeze for energy.</summary>
    public string Address { get; init; } = null!;

    /// <summary>The <c>KeyManagement:DevSecrets</c> key holding the throwaway signing key. Keep it COLON-FREE —
    /// a colon in a DevSecrets key is silently truncated by config's <c>GetChildren()</c> (a real trap hit before).</summary>
    public string SecretReference { get; init; } = null!;

    // EnergyPolicy thresholds (energy units, base-10 strings). Sensible dev defaults when omitted.
    public string? MinimumEnergy { get; init; }
    public string? TargetEnergy { get; init; }
    public string? StakeThreshold { get; init; }

    /// <summary>Whether auto-stake (StakeReplenish) tops the wallet's own energy up per policy. Default true.</summary>
    public bool EnableAutoStake { get; init; } = true;
}
