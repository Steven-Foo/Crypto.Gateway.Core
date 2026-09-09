using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class SuspendWalletRequest
{
    [MaxLength(512)] public string? Reason { get; init; }
}
