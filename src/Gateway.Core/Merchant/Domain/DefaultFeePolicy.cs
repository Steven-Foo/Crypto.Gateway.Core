using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Domain;

/// <summary>
/// The staff-editable default deposit/withdrawal pricing for one asset — a template, not tied to any
/// merchant. Its only consumer is the create-merchant screen, which reads this to pre-fill the fee inputs;
/// whatever the operator submits still goes through the normal <c>POST /merchants</c> path as an explicit,
/// per-merchant <see cref="MerchantAssetPolicy"/> like any manually-typed value. This entity has no bearing
/// on how an already-unpriced merchant's fee is resolved at charge time (that stays
/// <c>MerchantDefaultFee</c>/<c>Merchant:DefaultFee</c>, untouched).
///
/// <para>Reuses <see cref="FeeSchedule"/> purely for its validation (bps bounds, non-negative, storable) —
/// top-up pricing has no place here (a default template covers only what the create-merchant form asks for)
/// so it is always validated at zero and never persisted.</para>
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

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<DefaultFeePolicy> Create(Guid assetId, FeeSchedule fees, DateTimeOffset now) =>
        Result.Success(new DefaultFeePolicy(Guid.CreateVersion7(), assetId, fees, now));

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
