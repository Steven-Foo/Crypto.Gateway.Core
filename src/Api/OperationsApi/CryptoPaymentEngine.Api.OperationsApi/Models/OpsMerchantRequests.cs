using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

public sealed class CreateMerchantRequest
{
    [Required, MaxLength(64)] public string MerchantCode { get; init; } = null!;
    [Required, MaxLength(256)] public string Name { get; init; } = null!;
    [Url] public string? CallbackUrl { get; init; }
}

public sealed class SetMerchantStatusRequest
{
    [Required] public bool Active { get; init; }
}

public sealed class UpdateAllowedIpsRequest
{
    [Required] public List<string> IpAddresses { get; init; } = [];
}

public sealed class FailPaymentIntentRequest
{
    [Required, MaxLength(512)] public string Reason { get; init; } = null!;
}

/// <summary>
/// Declares a merchant's per-asset fee: a flat component in <b>display</b> units (converted to base units at
/// the edge) plus a percentage in basis points (1bp = 0.01%), for both deposit and withdrawal. A zero fixed
/// component is valid (pure-percentage pricing). Bounds are enforced by the domain <c>FeeSchedule</c>
/// (deposit bps &lt; 100%, withdrawal bps ≤ 100%, non-negative).
/// </summary>
public sealed class SetMerchantFeeRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal DepositFeeFixed { get; init; }
    public int DepositFeeBps { get; init; }
    public decimal WithdrawalFeeFixed { get; init; }
    public int WithdrawalFeeBps { get; init; }
}
