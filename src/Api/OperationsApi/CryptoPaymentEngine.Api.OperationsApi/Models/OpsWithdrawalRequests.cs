using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class RejectWithdrawalRequest
{
    [Required, MaxLength(512)] public string Reason { get; init; } = null!;
}
