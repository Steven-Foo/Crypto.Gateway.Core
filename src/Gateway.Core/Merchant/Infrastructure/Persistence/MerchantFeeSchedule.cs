using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>
/// Resolves a merchant's per-asset <see cref="FeeSchedule"/> and delegates the arithmetic to it. Loading
/// the policy entity (rather than projecting columns) keeps the internal rehydration encapsulated behind
/// the public <see cref="MerchantAssetPolicy.Fees"/>. A merchant with no explicit fee for the asset falls back
/// to the platform default (<see cref="MerchantDefaultFee"/>) — which is itself <c>None</c> unless configured,
/// so an unconfigured platform still charges an unpriced merchant nothing.
/// </summary>
public sealed class MerchantFeeSchedule(MerchantDbContext context, MerchantDefaultFee defaultFee) : IMerchantFeeSchedule
{
    public async Task<FeeQuote> QuoteDepositFeeAsync(
        Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default)
    {
        var schedule = await LoadFeesAsync(merchantId, assetId, cancellationToken);
        var (fee, minimumApplied) = schedule.QuoteDepositFeeDetailed(receivedAmount);
        return new FeeQuote(fee, schedule.DepositFeeBps, schedule.DepositFeeFixed, schedule.MinimumDepositFee, minimumApplied);
    }

    public async Task<FeeQuote> QuoteWithdrawalFeeAsync(
        Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default)
    {
        var schedule = await LoadFeesAsync(merchantId, assetId, cancellationToken);
        var (fee, minimumApplied) = schedule.QuoteWithdrawalFeeDetailed(amount);
        return new FeeQuote(fee, schedule.WithdrawalFeeBps, schedule.WithdrawalFee, schedule.MinimumWithdrawalFee, minimumApplied);
    }

    /// <summary>
    /// Prices a merchant top-up from the merchant's OWN schedule — deliberately bypassing the platform
    /// default that <see cref="LoadFeesAsync"/> applies. The default exists so an unpriced merchant is never
    /// silently free on customer deposits and withdrawals; applying it here would instead mean a merchant is
    /// silently charged to fund its own float, which is the opposite of the intended "top-up defaults to
    /// zero". So: no explicit top-up rate ⇒ no top-up fee, full stop.
    /// </summary>
    public async Task<BigInteger> QuoteTopUpFeeAsync(
        Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
        (await LoadOwnFeesAsync(merchantId, assetId, cancellationToken)).QuoteTopUpFee(receivedAmount);

    private async Task<FeeSchedule> LoadFeesAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken)
    {
        var resolved = await LoadOwnFeesAsync(merchantId, assetId, cancellationToken);

        // No explicit fee (no policy, or a cap/limits-only policy with a zero schedule) ⇒ the platform default,
        // so an unpriced merchant is never silently free. The default is itself None unless configured.
        return resolved.Equals(FeeSchedule.None) ? defaultFee.Schedule : resolved;
    }

    /// <summary>The merchant's own declared schedule, with no platform-default substitution.</summary>
    private async Task<FeeSchedule> LoadOwnFeesAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken)
    {
        var policy = await context.AssetPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.MerchantId == merchantId && p.AssetId == assetId, cancellationToken);

        return policy?.Fees ?? FeeSchedule.None;
    }
}
