using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

/// <summary>
/// A full replacement of one asset's default fee template — unlike <c>SetMerchantFeeRequest</c>, every field
/// is required (there is no "omit = unchanged" here; this is "edit the template", not "adjust one merchant's
/// pricing"). Same shape as <c>CreateMerchantFeeRequest</c> plus the chain/coin this template targets.
/// </summary>
public sealed class SetDefaultFeeRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;

    [Required] public decimal DepositFeeFixed { get; init; }

    /// <summary>Plain percent, e.g. <c>2</c> = 2%. At most 2 decimal places.</summary>
    [Required] public decimal DepositFeePercent { get; init; }

    [Required] public decimal DepositFeeMinimum { get; init; }
    [Required] public decimal WithdrawalFeeFixed { get; init; }
    [Required] public decimal WithdrawalFeePercent { get; init; }
    [Required] public decimal WithdrawalFeeMinimum { get; init; }
}

/// <summary>
/// A full replacement of one asset's PLATFORM-DEFAULT limits, in the coin's own units (e.g. USDT). Every field
/// is sent each time: <c>null</c> = no platform default for that bound (the deployed configuration applies),
/// <c>0</c> is a real value. A merchant's own value for a bound always wins over these.
/// </summary>
public sealed class SetDefaultLimitsRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;

    public decimal? MinimumDeposit { get; init; }
    public decimal? MaximumDeposit { get; init; }
    public decimal? MinimumWithdrawal { get; init; }
    public decimal? MaximumWithdrawal { get; init; }
    public decimal? ApprovalThreshold { get; init; }
}
