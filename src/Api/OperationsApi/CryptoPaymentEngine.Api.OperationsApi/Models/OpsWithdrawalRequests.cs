using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class RejectWithdrawalRequest
{
    [Required, MaxLength(512)] public string Reason { get; init; } = null!;
}

public sealed class CancelWithdrawalRequest
{
    [Required, MaxLength(512)] public string Reason { get; init; } = null!;
}

/// <summary>Why an admin declined a merchant settlement at audit. The merchant's reserved funds are released.</summary>
public sealed class RejectSettlementRequest
{
    [MaxLength(512)] public string? Reason { get; init; }
}

/// <summary>
/// A finance admin recording a merchant settlement they paid from a company wallet outside platform custody.
/// <see cref="TransactionHash"/> is verified on-chain before anything is written. <see cref="SourceAddress"/>
/// is recorded for the audit trail but deliberately not constrained — the admin pays from whichever company
/// wallet suits them.
/// </summary>
public sealed class RecordSettlementRequest
{
    [Required, MaxLength(128)] public string TransactionHash { get; init; } = null!;
    [MaxLength(128)] public string? SourceAddress { get; init; }
}
