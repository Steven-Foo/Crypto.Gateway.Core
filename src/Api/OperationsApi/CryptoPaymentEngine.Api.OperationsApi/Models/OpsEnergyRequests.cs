using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

/// <summary>Create the chain's staking (energy) wallet. Asking again returns the existing one.</summary>
public sealed class ProvisionStakingWalletRequest
{
    [Required] public string Chain { get; init; } = null!;

    /// <summary>Recorded on the audit entry.</summary>
    [MaxLength(256)] public string? Reason { get; init; }
}

/// <summary>
/// The staking wallet's rules, in TRON energy units (whole numbers). <see cref="TargetEnergy"/> must be at least
/// <see cref="MinimumEnergy"/>. With <see cref="EnableAutoStake"/> on, the system stakes more TRX whenever the
/// staking wallet's own energy falls to <see cref="StakeThreshold"/> or below.
/// </summary>
public sealed class SetStakingPolicyRequest
{
    [Required] public string Chain { get; init; } = null!;
    public long MinimumEnergy { get; init; }
    public long TargetEnergy { get; init; }
    public long StakeThreshold { get; init; }
    public bool EnableAutoStake { get; init; }

    /// <summary>Recorded on the audit entry.</summary>
    [MaxLength(256)] public string? Reason { get; init; }
}

/// <summary>Start unlocking staked TRX. <see cref="Amount"/> is in TRX (up to 6 decimal places).</summary>
public sealed class UnstakeRequest
{
    [Required] public string Chain { get; init; } = null!;
    public decimal Amount { get; init; }

    /// <summary>Recorded on the audit entry.</summary>
    [MaxLength(256)] public string? Reason { get; init; }
}

/// <summary>Move every finished unstake back into the staking wallet's spendable TRX.</summary>
public sealed class WithdrawUnstakedRequest
{
    [Required] public string Chain { get; init; } = null!;

    /// <summary>Recorded on the audit entry.</summary>
    [MaxLength(256)] public string? Reason { get; init; }
}
