using System.ComponentModel.DataAnnotations;

namespace CryptoPaymentEngine.Api.OperationsApi.Models;

/// <summary>
/// <c>MerchantCode</c> is deliberately absent — the backend mints it (<c>MerchantRegistrar</c> generates the
/// next sequential <c>ME#####</c> code), so a caller can no longer choose or collide with one. There is no
/// <c>callbackUrl</c> either — deposit/withdrawal webhook delivery resolves its target entirely from the
/// per-request <c>callbackUrl</c> on each individual API call, never from a merchant-level field, so nothing
/// is lost by not collecting one here.
/// </summary>
public sealed class CreateMerchantRequest
{
    [Required, MaxLength(256)] public string Name { get; init; } = null!;
    [MaxLength(256)] public string? ContactEmail { get; init; }
    [MaxLength(1024)] public string? Remark { get; init; }

    /// <summary>T+N settlement period in whole days (0-30; the UI offers 0/1/2 but the domain accepts the
    /// full config range).</summary>
    [Range(0, 30)]
    public int SettlementDays { get; init; }

    /// <summary>"auto" or "manual" (case-insensitive). Record only today, defaults to "manual" if omitted —
    /// see <c>SettlementMode</c>.</summary>
    public string? SettlementMode { get; init; }

    /// <summary>Only TRX/USDT is priceable today — omit to create the merchant unpriced (falls back to the
    /// platform default fee, exactly like before this endpoint accepted pricing at all); a caller passing
    /// any other chain/coin gets a clear rejection rather than a silently-inert policy.</summary>
    public CreateMerchantFeeRequest? Fees { get; init; }
}

/// <summary>The initial pricing set at merchant creation — the same shape as <see cref="SetMerchantFeeRequest"/>
/// minus chain/coin (defaulted to the one priceable asset today).</summary>
public sealed class CreateMerchantFeeRequest
{
    public decimal DepositFeeFixed { get; init; }
    public decimal DepositFeePercent { get; init; }
    public decimal DepositFeeMinimum { get; init; }
    public decimal WithdrawalFeeFixed { get; init; }
    public decimal WithdrawalFeePercent { get; init; }
    public decimal WithdrawalFeeMinimum { get; init; }
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
/// the edge) plus a percentage as a plain <b>percent</b> (e.g. <c>2</c> = 2%, at most 2 decimal places — the
/// endpoint converts to basis points internally), for both deposit and withdrawal. A zero fixed component is
/// valid (pure-percentage pricing). Bounds are enforced by the domain <c>FeeSchedule</c> (0-100%, non-negative).
/// <see cref="DepositFeeMinimum"/>/<see cref="WithdrawalFeeMinimum"/> are the 最低手续费 floor:
/// <c>max(fixed + amount×percent, minimum)</c>.
/// </summary>
public sealed class SetMerchantFeeRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal DepositFeeFixed { get; init; }

    /// <summary>Plain percent, e.g. <c>2</c> = 2%. At most 2 decimal places (finer values are rejected, never
    /// rounded — same "never truncate money" rule as an amount).</summary>
    public decimal DepositFeePercent { get; init; }

    /// <summary>最低手续费 — the deposit fee never charges less than this, even if fixed+percent comes out lower.</summary>
    public decimal DepositFeeMinimum { get; init; }

    public decimal WithdrawalFeeFixed { get; init; }
    public decimal WithdrawalFeePercent { get; init; }

    /// <summary>最低手续费 — the withdrawal fee never charges less than this.</summary>
    public decimal WithdrawalFeeMinimum { get; init; }

    /// <summary>Fee on a merchant top-up (the merchant funding its own balance). Defaults to zero, and
    /// deliberately never inherits the platform default fee — a merchant is not charged to fund its own
    /// float unless staff price it. Bounded [0, 100%] (it is deducted, not grossed up). No minimum-fee concept.</summary>
    public decimal TopUpFeeFixed { get; init; }
    public decimal TopUpFeePercent { get; init; }
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
/// <b>display</b> units (null = no flat cap) plus a percentage cap as a plain <b>percent</b> (e.g. <c>50</c> =
/// 50%, at most 2 decimal places — converted to basis points internally; 0 = no percent cap). Both unset ⇒ no
/// cap. Distinct from the user Min/MaxWithdrawal.</summary>
public sealed class SetWithdrawalCapRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal? FlatCap { get; init; }
    public decimal Percent { get; init; }
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

/// <summary>Sets the per-merchant <b>deposit (payin)</b> min/max for one asset, in <b>display</b> units. Null
/// on a bound = unset ⇒ minimum falls back to the platform's per-chain dust-floor config, maximum stays
/// unbounded (no platform-wide default exists for it today); a set value (including 0) fully overrides.
/// Mirrors <see cref="SetWithdrawalLimitsRequest"/> for the payin side.</summary>
public sealed class SetDepositLimitsRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
}

/// <summary>Updates a merchant's staff-facing profile fields. Every field is optional — the caller sends only
/// what changed; an omitted field is left unchanged, an explicit empty string clears <see cref="ContactEmail"/>/
/// <see cref="Remark"/>. Write-only: the response is just an ack (matches the withdrawal-limits pattern).</summary>
public sealed class UpdateMerchantProfileRequest
{
    [MaxLength(256)] public string? ContactEmail { get; init; }

    /// <summary>"auto" or "manual" (case-insensitive). Record only today — see <c>SettlementMode</c>.</summary>
    public string? SettlementMode { get; init; }

    [MaxLength(1024)] public string? Remark { get; init; }
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

/// <summary>
/// A staff-initiated manual credit or debit to a merchant's ledger balance — NOT backed by a real on-chain
/// deposit/withdrawal (e.g. a support-ticket correction). <see cref="Amount"/> is in <b>display</b> units,
/// converted to base units at this edge (§14); <see cref="Reason"/> is mandatory and lands in the journal
/// description + audit log. <see cref="AdjustmentId"/> is optional — omit it to let the Ledger mint a fresh
/// idempotency key, or supply a stable value (e.g. a support-ticket id) so a retried request replays safely
/// instead of double-posting.
/// </summary>
public sealed class AdjustMerchantBalanceRequest
{
    [Required, MaxLength(16)] public string Chain { get; init; } = null!;
    [Required, MaxLength(16)] public string Coin { get; init; } = null!;
    public decimal Amount { get; init; }
    [Required, MaxLength(512)] public string Reason { get; init; } = null!;
    public Guid? AdjustmentId { get; init; }
}
