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
    public async Task<BigInteger> QuoteDepositFeeAsync(
        Guid merchantId, Guid assetId, BigInteger receivedAmount, CancellationToken cancellationToken = default) =>
        (await LoadFeesAsync(merchantId, assetId, cancellationToken)).QuoteDepositFee(receivedAmount);

    public async Task<BigInteger> QuoteWithdrawalFeeAsync(
        Guid merchantId, Guid assetId, BigInteger amount, CancellationToken cancellationToken = default) =>
        (await LoadFeesAsync(merchantId, assetId, cancellationToken)).QuoteWithdrawalFee(amount);

    public async Task<Result<BigInteger>> GrossUpDepositAsync(
        Guid merchantId, Guid assetId, BigInteger netTarget, CancellationToken cancellationToken = default) =>
        (await LoadFeesAsync(merchantId, assetId, cancellationToken)).GrossUpForDeposit(netTarget);

    private async Task<FeeSchedule> LoadFeesAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken)
    {
        var policy = await context.AssetPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.MerchantId == merchantId && p.AssetId == assetId, cancellationToken);

        var resolved = policy?.Fees ?? FeeSchedule.None;

        // No explicit fee (no policy, or a cap/limits-only policy with a zero schedule) ⇒ the platform default,
        // so an unpriced merchant is never silently free. The default is itself None unless configured.
        return resolved.Equals(FeeSchedule.None) ? defaultFee.Schedule : resolved;
    }
}
