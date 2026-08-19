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
