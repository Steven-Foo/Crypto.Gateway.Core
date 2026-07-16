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
        BigInteger minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        FeeSchedule fees,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        AssetId = assetId;
        SweepThreshold = sweepThreshold;
        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        DepositFeeFixed = fees.DepositFeeFixed;
        DepositFeeBps = fees.DepositFeeBps;
        WithdrawalFee = fees.WithdrawalFee;
        WithdrawalFeeBps = fees.WithdrawalFeeBps;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private MerchantAssetPolicy() : base(Guid.Empty)
    {
    }

    public Guid MerchantId { get; private set; }
    public Guid AssetId { get; private set; }
    public BigInteger SweepThreshold { get; private set; }
    public BigInteger MinimumWithdrawal { get; private set; }

    /// <summary>Null means no upper bound. Zero would be indistinguishable from "unlimited".</summary>
    public BigInteger? MaximumWithdrawal { get; private set; }

    // ── Pricing (flat columns, rehydrated into a FeeSchedule via Fees) ──
    public BigInteger DepositFeeFixed { get; private set; }
    public int DepositFeeBps { get; private set; }
    public BigInteger WithdrawalFee { get; private set; }
    public int WithdrawalFeeBps { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The merchant's pricing for this asset. The single home of the fee arithmetic.</summary>
    public FeeSchedule Fees => FeeSchedule.FromTrusted(DepositFeeFixed, DepositFeeBps, WithdrawalFee, WithdrawalFeeBps);

    internal static Result<MerchantAssetPolicy> Create(
        Guid merchantId,
        Guid assetId,
        BigInteger sweepThreshold,
        BigInteger minimumWithdrawal,
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
        BigInteger minimumWithdrawal,
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
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    private static Result ValidateLimits(
        BigInteger sweepThreshold,
        BigInteger minimumWithdrawal,
        BigInteger? maximumWithdrawal)
    {
        BigInteger?[] amounts = [sweepThreshold, minimumWithdrawal, maximumWithdrawal];

        foreach (var amount in amounts)
        {
            if (amount is not { } value)
                continue;

            if (value < BigInteger.Zero)
                return Result.Failure(MerchantErrors.AmountNegative);

            if (!MoneyLimits.IsStorable(value))
                return Result.Failure(MerchantErrors.AmountTooLarge);
        }

        if (maximumWithdrawal is { } max && minimumWithdrawal > max)
            return Result.Failure(MerchantErrors.WithdrawalRangeInvalid);

        return Result.Success();
    }
}
