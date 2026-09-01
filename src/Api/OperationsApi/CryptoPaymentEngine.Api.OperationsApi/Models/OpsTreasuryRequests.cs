using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

/// <summary>Register the cold treasury address for a chain — a watch-only address whose key the system never
/// holds (a human signs outbound transfers, §10).</summary>
public sealed class RegisterColdWalletRequest
{
    [Required] public string Chain { get; init; } = null!;
    [Required] public string Address { get; init; } = null!;
}

/// <summary>Initiate a treasury→hot reload: build the unsigned transfer to the operator-chosen pool wallet.
/// <see cref="Amount"/> is a display decimal — converted to base units at the edge using the asset's decimals (§14).</summary>
public sealed class InitiateReloadRequest
{
    [Required] public string Chain { get; init; } = null!;
    [Required] public Guid TargetWalletId { get; init; }
    [Required, Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
    public decimal Amount { get; init; }
}

/// <summary>Submit the operator's client-side-signed reload blob (hex). The cold key never reaches the backend (§10).</summary>
public sealed class SubmitReloadRequest
{
    [Required] public string SignedHex { get; init; } = null!;
}

/// <summary>
/// Record company funds already moved into a hot withdrawal wallet, so the automated payout pipeline stays
/// funded. <see cref="Amount"/> is a display decimal converted at the edge (§14); the verified on-chain amount
/// is what is actually booked. <see cref="SourceAddress"/> is the company wallet the funds came from — outside
/// platform custody, recorded for audit, deliberately unconstrained.
/// </summary>
public sealed class RecordHotWalletTopUpRequest
{
    [Required] public string Chain { get; init; } = null!;
    [Required] public Guid TargetWalletId { get; init; }
    [Required, Range(0.000001, double.MaxValue, ErrorMessage = "Amount must be greater than 0.")]
    public decimal Amount { get; init; }
    [Required, MaxLength(128)] public string TransactionHash { get; init; } = null!;
    [MaxLength(128)] public string? SourceAddress { get; init; }
}
