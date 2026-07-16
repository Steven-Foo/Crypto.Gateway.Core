using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

/// <summary>
/// Read-only projection for the hosted pay page. Computes the <em>effective</em> status so a lapsed-but-not-
/// yet-swept invoice already reads as "expired", matching what the payer should see.
/// </summary>
public sealed class PaymentIntentDirectory(PaymentIntentDbContext context, TimeProvider timeProvider) : IPaymentIntentDirectory
{
    public async Task<PaymentIntentView?> FindByPublicReferenceAsync(Guid publicReference, CancellationToken cancellationToken = default)
    {
        var intent = await context.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.PublicReference == publicReference, cancellationToken);

        if (intent is null)
            return null;

        var status = intent.Status switch
        {
            PaymentIntentStatus.Matched => "confirmed",
            PaymentIntentStatus.Expired => "expired",
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
