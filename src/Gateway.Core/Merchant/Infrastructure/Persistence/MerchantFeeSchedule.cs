using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Infrastructure.Persistence;

/// <summary>
/// Resolves a merchant's per-asset <see cref="FeeSchedule"/> and delegates the arithmetic to it. Loading
/// the policy entity (rather than projecting columns) keeps the internal rehydration encapsulated behind
/// the public <see cref="MerchantAssetPolicy.Fees"/>; a missing policy means an unpriced merchant → no fee.
/// </summary>
public sealed class MerchantFeeSchedule(MerchantDbContext context) : IMerchantFeeSchedule
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

        return policy?.Fees ?? FeeSchedule.None;
    }
}
