using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// The PLATFORM DEFAULT pricing and limits for one asset, edited by staff in the back office. It is what a
/// merchant gets for anything that merchant has not been given a value of its own:
/// <list type="bullet">
///   <item><b>Fees:</b> a merchant with no fee of its own for the asset is charged these (when they are not all
///   zero; an all-zero row falls through to the <c>Merchant:DefaultFee</c> config).</item>
///   <item><b>Limits:</b> each bound (deposit min/max, payout min/max, approval threshold) applies on its own
///   wherever the merchant has not set that bound. Null = no platform default for that bound, so the deployed
///   config value applies.</item>
/// </list>
/// Order of precedence, everywhere: the merchant's own value → this row → configuration.
///
/// <para>The create-merchant screen also reads it to pre-fill the fee inputs. Top-up pricing is deliberately
/// absent: a merchant is never charged to fund its own balance unless staff set a top-up fee for that merchant.</para>
/// </summary>
public sealed class DefaultFeePolicy : Entity<Guid>
{
    private DefaultFeePolicy(Guid id, Guid assetId, FeeSchedule fees, DateTimeOffset createdAt) : base(id)
    {
        AssetId = assetId;
        DepositFeeFixed = fees.DepositFeeFixed;
        DepositFeeBps = fees.DepositFeeBps;
        MinimumDepositFee = fees.MinimumDepositFee;
        WithdrawalFee = fees.WithdrawalFee;
        WithdrawalFeeBps = fees.WithdrawalFeeBps;
        MinimumWithdrawalFee = fees.MinimumWithdrawalFee;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    private DefaultFeePolicy() : base(Guid.Empty)
    {
    }

    /// <summary>The Blockchain module's canonical asset reference — opaque, no FK (§4.5), same as every
    /// other cross-module asset reference in this module.</summary>
    public Guid AssetId { get; private set; }

    public BigInteger DepositFeeFixed { get; private set; }
    public int DepositFeeBps { get; private set; }
    public BigInteger MinimumDepositFee { get; private set; }
    public BigInteger WithdrawalFee { get; private set; }
    public int WithdrawalFeeBps { get; private set; }
    public BigInteger MinimumWithdrawalFee { get; private set; }

    /// <summary>Platform default smallest deposit (payin) invoice, base units. Null = use configuration.</summary>
    public BigInteger? MinimumDeposit { get; private set; }

    /// <summary>Platform default largest deposit invoice, base units. Null = no platform maximum.</summary>
    public BigInteger? MaximumDeposit { get; private set; }

    /// <summary>Platform default smallest user payout, base units. Null = use configuration.</summary>
    public BigInteger? MinimumWithdrawal { get; private set; }

    /// <summary>Platform default largest user payout, base units. Null = use configuration.</summary>
    public BigInteger? MaximumWithdrawal { get; private set; }

    /// <summary>Platform default approval threshold (payouts and cash-outs above it need staff), base units.
    /// Null = use configuration.</summary>
    public BigInteger? ApprovalThreshold { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The default fees, or <see cref="FeeSchedule.None"/> when every component is zero.</summary>
    public FeeSchedule Fees =>
        FeeSchedule.Create(DepositFeeFixed, DepositFeeBps, WithdrawalFee, WithdrawalFeeBps,
            BigInteger.Zero, 0, MinimumDepositFee, MinimumWithdrawalFee) is { IsSuccess: true } fees
            ? fees.Value
            : FeeSchedule.None;

    public static Result<DefaultFeePolicy> Create(Guid assetId, FeeSchedule fees, DateTimeOffset now) =>
        Result.Success(new DefaultFeePolicy(Guid.CreateVersion7(), assetId, fees, now));

    /// <summary>A row holding limits only (no default fees yet). Its zero fees fall through to configuration.</summary>
    public static Result<DefaultFeePolicy> CreateForLimits(Guid assetId, DateTimeOffset now) =>
        Result.Success(new DefaultFeePolicy(Guid.CreateVersion7(), assetId, FeeSchedule.None, now));

    /// <summary>Sets every platform-default limit at once. Each null clears that default (config applies).</summary>
    public Result SetLimits(
        BigInteger? minimumDeposit, BigInteger? maximumDeposit, BigInteger? minimumWithdrawal,
        BigInteger? maximumWithdrawal, BigInteger? approvalThreshold, DateTimeOffset now)
    {
        foreach (var amount in new[] { minimumDeposit, maximumDeposit, minimumWithdrawal, maximumWithdrawal, approvalThreshold })
        {
            if (amount is not { } value)
                continue;
            if (value < BigInteger.Zero)
                return Result.Failure(MerchantErrors.AmountNegative);
            if (!MoneyLimits.IsStorable(value))
                return Result.Failure(MerchantErrors.AmountTooLarge);
        }

        if (minimumDeposit is { } depositMin && maximumDeposit is { } depositMax && depositMin > depositMax)
            return Result.Failure(MerchantErrors.DepositRangeInvalid);
        if (minimumWithdrawal is { } payoutMin && maximumWithdrawal is { } payoutMax && payoutMin > payoutMax)
            return Result.Failure(MerchantErrors.WithdrawalRangeInvalid);

        MinimumDeposit = minimumDeposit;
        MaximumDeposit = maximumDeposit;
        MinimumWithdrawal = minimumWithdrawal;
        MaximumWithdrawal = maximumWithdrawal;
        ApprovalThreshold = approvalThreshold;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Update(FeeSchedule fees, DateTimeOffset now)
    {
        DepositFeeFixed = fees.DepositFeeFixed;
        DepositFeeBps = fees.DepositFeeBps;
        MinimumDepositFee = fees.MinimumDepositFee;
        WithdrawalFee = fees.WithdrawalFee;
        WithdrawalFeeBps = fees.WithdrawalFeeBps;
        MinimumWithdrawalFee = fees.MinimumWithdrawalFee;
        UpdatedAt = now;
        return Result.Success();
    }
}
