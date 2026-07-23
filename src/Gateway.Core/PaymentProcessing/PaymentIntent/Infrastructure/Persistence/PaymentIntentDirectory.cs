using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using Microsoft.EntityFrameworkCore;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

/// <summary>
/// Read-only projection for the hosted pay page and the merchant transaction-query endpoint. Computes the
/// <em>effective</em> status so a lapsed-but-not-yet-swept invoice already reads as "expired", matching what
/// the payer/merchant should see.
/// </summary>
public sealed class PaymentIntentDirectory(PaymentIntentDbContext context, TimeProvider timeProvider) : IPaymentIntentDirectory
{
    public async Task<PaymentIntentView?> FindByPublicReferenceAsync(Guid publicReference, CancellationToken cancellationToken = default)
    {
        var intent = await context.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.PublicReference == publicReference, cancellationToken);

        return intent is null ? null : ToView(intent);
    }

    public async Task<PaymentIntentView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default)
    {
        var intent = await context.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(
                i => i.MerchantId == merchantId && i.MerchantTransactionId == merchantTransactionId, cancellationToken);

        return intent is null ? null : ToView(intent);
    }

    public Task<Guid?> FindMatchedDepositIdAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents.AsNoTracking()
            .Where(i => i.MerchantId == merchantId && i.MerchantTransactionId == merchantTransactionId)
            .Select(i => i.MatchedDepositId)
            .SingleOrDefaultAsync(cancellationToken);

    private PaymentIntentView ToView(PaymentIntentEntity intent)
    {
        var status = intent.Status switch
        {
            PaymentIntentStatus.Matched => "confirmed",
            PaymentIntentStatus.Expired => "expired",
            PaymentIntentStatus.Failed => "failed",
            _ => timeProvider.GetUtcNow() >= intent.ExpiresAt ? "expired" : "pending",
        };

        return new PaymentIntentView(
            intent.PublicReference,
            intent.AssetId,
            intent.Address,
            intent.ExpectedAmount.ToString(CultureInfo.InvariantCulture),
            status,
            intent.ExpiresAt);
    }
}
