using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.MerchantGateway.Models;

/// <summary>Frozen deposit-request body. <see cref="ExpectedAmount"/> is a display decimal — converted to
/// base units at the edge (§14).</summary>
public sealed class DepositRequest
{
    [Required] public string PaymentMethod { get; init; } = "usdt";
    [Required] public string TransactionId { get; init; } = null!;
    [Required] public string UserId { get; init; } = null!;
    [Required, Range(0.000001, double.MaxValue, ErrorMessage = "ExpectedAmount must be greater than 0.")]
    public decimal ExpectedAmount { get; init; }
    [Required, Url] public string CallbackUrl { get; init; } = null!;
}

/// <summary>Frozen withdraw-request body. <see cref="Amount"/> is a display decimal — the destination receives
/// it in full; the merchant is debited amount + fee at the ledger (on-top fee).</summary>
public sealed class WithdrawRequest
{
    [Required] public string PaymentMethod { get; init; } = "usdt";
    [Required] public string TransactionId { get; init; } = null!;
    [Required, RegularExpression(@"^T[A-Za-z0-9]{33}$", ErrorMessage = "ToAddress must be a valid TRC-20 (TRON) address.")]
    public string ToAddress { get; init; } = null!;
    [Required, Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
    public decimal Amount { get; init; }
    [Required, Url] public string CallbackUrl { get; init; } = null!;
}
