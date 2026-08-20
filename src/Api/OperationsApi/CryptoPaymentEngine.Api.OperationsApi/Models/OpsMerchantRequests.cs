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

/// <summary>Sets a merchant's settlement period (T+N) in whole days (0 = T+0). Gates the withdrawable balance
/// on both user payouts and the merchant cash-out. Domain-validated 0–30.</summary>
public sealed class SetSettlementPeriodRequest
{
    [Range(0, 30)] public int Days { get; init; }
}

/// <summary>Registers/updates the merchant's whitelisted cash-out (settlement) wallet for a chain.</summary>
public sealed class SetSettlementWalletRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(128)] public string Address { get; init; } = null!;
}

/// <summary>Sets the merchant-withdrawal (cash-out) liquidity cap for one asset: an optional flat cap in
/// <b>display</b> units (null = no flat cap) plus a percentage cap in basis points (0 = no percent cap). Both
/// unset ⇒ no cap. Distinct from the user Min/MaxWithdrawal.</summary>
public sealed class SetWithdrawalCapRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal? FlatCap { get; init; }
    public int PercentBps { get; init; }
}

/// <summary>Sets the per-merchant <b>user-withdrawal</b> min/max for one asset, in <b>display</b> units. Null on
/// a bound = unset ⇒ the platform config limit (<c>Withdrawal:Policies</c>) applies for that bound; a set value
/// (including 0 = "no minimum") overrides. Distinct from the cash-out cap.</summary>
public sealed class SetWithdrawalLimitsRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
}

/// <summary>Sets the per-merchant approval threshold for one asset, in <b>display</b> units. Null = unset ⇒ the
/// platform config threshold (<c>Withdrawal:Policies</c>) applies; a set value (including 0 = "everything needs
/// approval") overrides. A withdrawal above it — user payout OR cash-out — needs human oversight (§10).</summary>
public sealed class SetApprovalThresholdRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal? Threshold { get; init; }
}
