using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// Per-merchant, per-asset limits. All amounts are unsigned integer base units (§14) —
/// <c>AssetId</c> is an opaque cross-module reference into the Blockchain module's catalog,
/// deliberately without a foreign key (§4.5).
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
        BigInteger withdrawalFee,
        DateTimeOffset createdAt) : base(id)
    {
        MerchantId = merchantId;
        AssetId = assetId;
        SweepThreshold = sweepThreshold;
        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        WithdrawalFee = withdrawalFee;
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

    public BigInteger WithdrawalFee { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    internal static Result<MerchantAssetPolicy> Create(
        Guid merchantId,
        Guid assetId,
        BigInteger sweepThreshold,
        BigInteger minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        BigInteger withdrawalFee,
        DateTimeOffset createdAt)
    {
        var validation = Validate(sweepThreshold, minimumWithdrawal, maximumWithdrawal, withdrawalFee);
        if (validation.IsFailure)
            return Result.Failure<MerchantAssetPolicy>(validation.Error!);

        return Result.Success(new MerchantAssetPolicy(
            Guid.CreateVersion7(), merchantId, assetId,
            sweepThreshold, minimumWithdrawal, maximumWithdrawal, withdrawalFee, createdAt));
    }

    internal Result Update(
        BigInteger sweepThreshold,
        BigInteger minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        BigInteger withdrawalFee,
        DateTimeOffset updatedAt)
    {
        var validation = Validate(sweepThreshold, minimumWithdrawal, maximumWithdrawal, withdrawalFee);
        if (validation.IsFailure)
            return validation;

        SweepThreshold = sweepThreshold;
        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        WithdrawalFee = withdrawalFee;
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    private static Result Validate(
        BigInteger sweepThreshold,
        BigInteger minimumWithdrawal,
        BigInteger? maximumWithdrawal,
        BigInteger withdrawalFee)
    {
        BigInteger?[] amounts = [sweepThreshold, minimumWithdrawal, maximumWithdrawal, withdrawalFee];

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
