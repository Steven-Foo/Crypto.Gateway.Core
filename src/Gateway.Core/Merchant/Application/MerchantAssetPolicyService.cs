using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Application;

/// <summary>
/// A merchant's declared pricing for one asset — the staff-set flat+% fee, read back for pre-fill. Amounts
/// are exact base-unit integer strings (§14); the host converts to/from display at the edge. Bps are
/// basis points (1bp = 0.01%).
/// </summary>
public sealed record MerchantAssetPolicyView(
    Guid AssetId,
    string DepositFeeFixed,
    int DepositFeeBps,
    string WithdrawalFee,
    int WithdrawalFeeBps);

/// <summary>
/// Staff-facing pricing management — the write path that was missing, so a merchant's <c>fixed + %</c> fee
/// can actually be declared and persisted (until now <see cref="Domain.Merchant.SetAssetPolicy"/> had no
/// production caller and every merchant was unpriced ⇒ zero fee). Deliberately separate from
/// <see cref="IMerchantRegistrar"/> (identity/credentials): pricing is its own capability (SRP §12).
///
/// <para>v1 sets <b>pricing only</b>. The policy row also carries operational limits (sweep threshold,
/// min/max withdrawal), but those are not consulted by the withdrawal/sweep flows yet (they read config),
/// so this service <b>preserves</b> any existing limit values rather than exposing numbers the system would
/// ignore — per-merchant limits + their wiring are a Platform-UI follow-up.</para>
/// </summary>
public interface IMerchantAssetPolicyService
{
    /// <summary>
    /// Declares (upserts) the merchant's deposit + withdrawal fee for one asset. Amounts are base units.
    /// Existing operational limits on the policy are preserved. Fails with the domain validation error if the
    /// schedule is invalid (negative, over-large, or a bps out of range), or if the merchant is absent/closed.
    /// </summary>
    Task<Result> SetFeesAsync(
        Guid merchantId,
        Guid assetId,
        BigInteger depositFeeFixed,
        int depositFeeBps,
        BigInteger withdrawalFee,
        int withdrawalFeeBps,
        CancellationToken cancellationToken = default);

    /// <summary>The merchant's current per-asset pricing — for staff to read / a UI to pre-fill. Empty if unpriced.</summary>
    Task<Result<IReadOnlyList<MerchantAssetPolicyView>>> ListAsync(
        Guid merchantId, CancellationToken cancellationToken = default);
}

public sealed class MerchantAssetPolicyService(IMerchantRepository repository, TimeProvider timeProvider)
    : IMerchantAssetPolicyService
{
    public async Task<Result> SetFeesAsync(
        Guid merchantId,
        Guid assetId,
        BigInteger depositFeeFixed,
        int depositFeeBps,
        BigInteger withdrawalFee,
        int withdrawalFeeBps,
        CancellationToken cancellationToken = default)
    {
        // Validate pricing in the domain (bps bounds, non-negative, deposit bps < 100%) before touching state.
        var fees = FeeSchedule.Create(depositFeeFixed, depositFeeBps, withdrawalFee, withdrawalFeeBps);
        if (fees.IsFailure)
            return Result.Failure(fees.Error!);

        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure(MerchantErrors.NotFound);

        // Preserve existing operational limits — v1 sets the price, not the limits (which come from the
        // Platform UI later and aren't enforced yet). A first-time policy gets benign defaults.
        var existing = merchant.AssetPolicies.SingleOrDefault(p => p.AssetId == assetId);
        var sweepThreshold = existing?.SweepThreshold ?? BigInteger.Zero;
        var minimumWithdrawal = existing?.MinimumWithdrawal ?? BigInteger.Zero;
        var maximumWithdrawal = existing?.MaximumWithdrawal;

        var result = merchant.SetAssetPolicy(
            assetId, sweepThreshold, minimumWithdrawal, maximumWithdrawal, fees.Value, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<MerchantAssetPolicyView>>> ListAsync(
        Guid merchantId, CancellationToken cancellationToken = default)
    {
        var merchant = await repository.GetByIdAsync(merchantId, cancellationToken);
        if (merchant is null)
            return Result.Failure<IReadOnlyList<MerchantAssetPolicyView>>(MerchantErrors.NotFound);

        IReadOnlyList<MerchantAssetPolicyView> views = merchant.AssetPolicies
            .Select(p => new MerchantAssetPolicyView(
                p.AssetId,
                p.DepositFeeFixed.ToString(CultureInfo.InvariantCulture),
                p.DepositFeeBps,
                p.WithdrawalFee.ToString(CultureInfo.InvariantCulture),
                p.WithdrawalFeeBps))
            .ToList();

        return Result.Success(views);
    }
}
