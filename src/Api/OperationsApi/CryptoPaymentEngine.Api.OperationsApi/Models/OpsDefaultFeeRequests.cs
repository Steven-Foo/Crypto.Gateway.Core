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
