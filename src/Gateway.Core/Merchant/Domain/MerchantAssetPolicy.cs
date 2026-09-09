using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// Per-merchant, per-asset limits and pricing. All amounts are unsigned integer base units (§14) —
/// <c>AssetId</c> is an opaque cross-module reference into the Blockchain module's catalog,
/// deliberately without a foreign key (§4.5).
///
/// Operational limits (sweep threshold, min/max withdrawal) and pricing (the <see cref="FeeSchedule"/>)
/// live together on the policy but are conceptually distinct: limits gate a transaction, the schedule
/// prices it. The fee columns are stored flat and rehydrated into a <see cref="FeeSchedule"/> via
/// <see cref="Fees"/>, which owns the money math.
/// </summary>
public sealed class MerchantAssetPolicy : Entity<Guid>
{
    private MerchantAssetPolicy(
        Guid id,
        Guid merchantId,
        Guid assetId,
        BigInteger sweepThreshold,
        BigInteger? minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        FeeSchedule fees,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        AssetId = assetId;
        SweepThreshold = sweepThreshold;
        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        MinimumDeposit = null;                  // unset ⇒ the invoice flow falls back to the platform dust-floor config
        MaximumDeposit = null;                  // unset ⇒ no platform-wide default exists, so it stays unbounded
        DepositFeeFixed = fees.DepositFeeFixed;
        DepositFeeBps = fees.DepositFeeBps;
        WithdrawalFee = fees.WithdrawalFee;
        WithdrawalFeeBps = fees.WithdrawalFeeBps;
        TopUpFeeFixed = fees.TopUpFeeFixed;
        TopUpFeeBps = fees.TopUpFeeBps;
        MinimumDepositFee = fees.MinimumDepositFee;
        MinimumWithdrawalFee = fees.MinimumWithdrawalFee;
        MerchantWithdrawalFlatCap = null;       // no merchant-withdrawal (cash-out) cap until one is set
        MerchantWithdrawalPercentBps = 0;
        ApprovalThreshold = null;               // unset ⇒ the withdrawal flow uses the platform config threshold
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private MerchantAssetPolicy() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public Guid AssetId { get; private set; }
    public BigInteger SweepThreshold { get; private set; }

    /// <summary>Per-merchant user-withdrawal <b>minimum</b> override. Null = unset ⇒ the flow falls back to the
    /// platform config minimum (`Withdrawal:Policies`). A set value (including 0 = "no minimum") fully overrides.</summary>
    public BigInteger? MinimumWithdrawal { get; private set; }

    /// <summary>Per-merchant user-withdrawal <b>maximum</b> override. Null = unset ⇒ the flow falls back to the
    /// platform config maximum. A set value overrides. (Zero would be indistinguishable from "unlimited", hence null.)</summary>
    public BigInteger? MaximumWithdrawal { get; private set; }

    /// <summary>Per-merchant deposit (payin) <b>minimum</b> — the smallest invoice amount the merchant may
    /// request. Null = unset ⇒ the PaymentIntent flow falls back to the platform's per-chain dust-floor config.
    /// A set value (including 0) fully overrides.</summary>
    public BigInteger? MinimumDeposit { get; private set; }

    /// <summary>Per-merchant deposit (payin) <b>maximum</b>. Null = unset ⇒ no platform-wide default exists
    /// today, so the invoice stays unbounded until this is set.</summary>
    public BigInteger? MaximumDeposit { get; private set; }

    // ── Pricing (flat columns, rehydrated into a FeeSchedule via Fees) ──
    public BigInteger DepositFeeFixed { get; private set; }
    public int DepositFeeBps { get; private set; }
    public BigInteger WithdrawalFee { get; private set; }
    public int WithdrawalFeeBps { get; private set; }

    /// <summary>Floor under the deposit fee (最低手续费) — see <see cref="FeeSchedule.MinimumDepositFee"/>.</summary>
    public BigInteger MinimumDepositFee { get; private set; }

    /// <summary>Floor under the withdrawal fee (最低手续费) — see <see cref="FeeSchedule.MinimumWithdrawalFee"/>.</summary>
    public BigInteger MinimumWithdrawalFee { get; private set; }

    /// <summary>Fee on a merchant top-up (funding its own balance). Zero unless staff declared a rate —
    /// deliberately never inherits the platform default, so a merchant is not charged to fund its own float.</summary>
    public BigInteger TopUpFeeFixed { get; private set; }
    public int TopUpFeeBps { get; private set; }

    // ── Merchant-withdrawal (earnings cash-out) liquidity cap — DISTINCT from the user Min/MaxWithdrawal
    // above, which gate a standard user payout. Null flat + 0 bps = no cap (cash out up to the full balance). ──
    /// <summary>Flat per-cash-out cap in base units; null = no flat cap.</summary>
    public BigInteger? MerchantWithdrawalFlatCap { get; private set; }

    /// <summary>Per-cash-out cap as a percentage of available balance, in basis points; 0 = no percent cap.</summary>
    public int MerchantWithdrawalPercentBps { get; private set; }

    /// <summary>Per-merchant, per-asset override of the platform config <b>approval threshold</b> — the payout
    /// amount above which a withdrawal (user payout OR cash-out) needs human oversight (approve at request +
    /// release at processing, §10). Null = unset ⇒ the flow uses the config threshold; a set value (including
    /// 0 = "everything needs approval") fully overrides. Applies to BOTH withdrawal kinds.</summary>
    public BigInteger? ApprovalThreshold { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The merchant's pricing for this asset. The single home of the fee arithmetic.</summary>
    public FeeSchedule Fees => FeeSchedule.FromTrusted(
        DepositFeeFixed, DepositFeeBps, WithdrawalFee, WithdrawalFeeBps, TopUpFeeFixed, TopUpFeeBps,
        MinimumDepositFee, MinimumWithdrawalFee);

    internal static Result<MerchantAssetPolicy> Create(
        Guid merchantId,
        Guid assetId,
        BigInteger sweepThreshold,
        BigInteger? minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        FeeSchedule fees,
        DateTimeOffset createdAt)
    {
        var validation = ValidateLimits(sweepThreshold, minimumWithdrawal, maximumWithdrawal);
        if (validation.IsFailure)
            return Result.Failure<MerchantAssetPolicy>(validation.Error!);

        return Result.Success(new MerchantAssetPolicy(
            Guid.CreateVersion7(), merchantId, assetId,
            sweepThreshold, minimumWithdrawal, maximumWithdrawal, fees, createdAt));
    }

    internal Result Update(
        BigInteger sweepThreshold,
        BigInteger? minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        FeeSchedule fees,
        DateTimeOffset updatedAt)
    {
        var validation = ValidateLimits(sweepThreshold, minimumWithdrawal, maximumWithdrawal);
        if (validation.IsFailure)
            return validation;

        SweepThreshold = sweepThreshold;
        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        DepositFeeFixed = fees.DepositFeeFixed;
        DepositFeeBps = fees.DepositFeeBps;
        WithdrawalFee = fees.WithdrawalFee;
        WithdrawalFeeBps = fees.WithdrawalFeeBps;
        TopUpFeeFixed = fees.TopUpFeeFixed;
        TopUpFeeBps = fees.TopUpFeeBps;
        MinimumDepositFee = fees.MinimumDepositFee;
        MinimumWithdrawalFee = fees.MinimumWithdrawalFee;
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    /// <summary>Sets the per-merchant deposit (payin) min/max independently of fees, cap, and the withdrawal
    /// limits. Null = unset ⇒ the PaymentIntent flow falls back to platform defaults (min: dust-floor config;
    /// max: unbounded). Validates non-negative, storable, and min ≤ max (when both are set).</summary>
    internal Result SetDepositLimits(BigInteger? minimumDeposit, BigInteger? maximumDeposit, DateTimeOffset now)
    {
        var validation = ValidateLimits(SweepThreshold, MinimumWithdrawal, MaximumWithdrawal, minimumDeposit, maximumDeposit);
        if (validation.IsFailure)
            return validation;

        MinimumDeposit = minimumDeposit;
        MaximumDeposit = maximumDeposit;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Sets the per-merchant user-withdrawal min/max override independently of fees, cap, and sweep.
    /// Null = unset ⇒ the withdrawal flow falls back to the platform config limit. Validates non-negative,
    /// storable, and min ≤ max (when both are set).</summary>
    internal Result SetWithdrawalLimits(BigInteger? minimumWithdrawal, BigInteger? maximumWithdrawal, DateTimeOffset now)
    {
        var validation = ValidateLimits(SweepThreshold, minimumWithdrawal, maximumWithdrawal);
        if (validation.IsFailure)
            return validation;

        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Sets the merchant-withdrawal (cash-out) liquidity cap independently of fees and user limits.
    /// A null flat cap and/or 0 bps disables that side of the cap.</summary>
    internal Result SetMerchantWithdrawalCap(BigInteger? flatCap, int percentBps, DateTimeOffset now)
    {
        if (flatCap is { } cap)
        {
            if (cap < BigInteger.Zero)
                return Result.Failure(MerchantErrors.AmountNegative);
            if (!MoneyLimits.IsStorable(cap))
                return Result.Failure(MerchantErrors.AmountTooLarge);
        }

        if (percentBps < 0 || percentBps > FeeSchedule.MaxBps)
            return Result.Failure(MerchantErrors.WithdrawalCapBpsInvalid);

        MerchantWithdrawalFlatCap = flatCap;
        MerchantWithdrawalPercentBps = percentBps;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Sets the per-merchant approval-threshold override independently of fees, limits, cap, and sweep.
    /// Null = unset ⇒ the withdrawal flow uses the platform config threshold. Validates non-negative + storable.</summary>
    internal Result SetApprovalThreshold(BigInteger? approvalThreshold, DateTimeOffset now)
    {
        if (approvalThreshold is { } threshold)
        {
            if (threshold < BigInteger.Zero)
                return Result.Failure(MerchantErrors.AmountNegative);
            if (!MoneyLimits.IsStorable(threshold))
                return Result.Failure(MerchantErrors.AmountTooLarge);
        }

        ApprovalThreshold = approvalThreshold;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result ValidateLimits(
        BigInteger sweepThreshold,
        BigInteger? minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        BigInteger? minimumDeposit = null,
        BigInteger? maximumDeposit = null)
    {
        BigInteger?[] amounts = [sweepThreshold, minimumWithdrawal, maximumWithdrawal, minimumDeposit, maximumDeposit];

        foreach (var amount in amounts)
        {
            if (amount is not { } value)
                continue;

            if (value < BigInteger.Zero)
                return Result.Failure(MerchantErrors.AmountNegative);

            if (!MoneyLimits.IsStorable(value))
                return Result.Failure(MerchantErrors.AmountTooLarge);
        }

        if (maximumWithdrawal is { } max && minimumWithdrawal is { } min && min > max)
            return Result.Failure(MerchantErrors.WithdrawalRangeInvalid);

        if (maximumDeposit is { } depositMax && minimumDeposit is { } depositMin && depositMin > depositMax)
            return Result.Failure(MerchantErrors.DepositRangeInvalid);

        return Result.Success();
    }
}
