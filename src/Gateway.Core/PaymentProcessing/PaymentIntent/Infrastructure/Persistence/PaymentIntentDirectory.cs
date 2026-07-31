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

    public async Task<(IReadOnlyList<PaymentIntentAdminRow> Items, int TotalCount)> SearchAsync(
        PaymentIntentAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.PaymentIntents.AsNoTracking()
            .Where(i => filter.MerchantId == null || i.MerchantId == filter.MerchantId)
            .Where(i => filter.SystemOrderNumber == null || i.PublicReference == filter.SystemOrderNumber)
            .Where(i => filter.MerchantOrderNumber == null || i.MerchantTransactionId == filter.MerchantOrderNumber)
            .Where(i => filter.ReceivingAddress == null || i.Address == filter.ReceivingAddress)
            .Where(i => filter.Network == null || i.Chain == filter.Network)
            .Where(i => filter.AssetId == null || i.AssetId == filter.AssetId)
            .Where(i => filter.FromDate == null || i.CreatedAt >= filter.FromDate)
            .Where(i => filter.ToDate == null || i.CreatedAt <= filter.ToDate);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items.Select(ToAdminRow).ToList(), totalCount);
    }

    private PaymentIntentView ToView(PaymentIntentEntity intent) => new(
        intent.PublicReference,
        intent.AssetId,
        intent.Address,
        intent.ExpectedAmount.ToString(CultureInfo.InvariantCulture),
        EffectiveStatus(intent),
        intent.ExpiresAt);

    private PaymentIntentAdminRow ToAdminRow(PaymentIntentEntity intent) => new(
        intent.MerchantId,
        intent.PublicReference,
        intent.MerchantTransactionId,
        intent.Chain,
        intent.AssetId,
        intent.Address,
        intent.ExpectedAmount.ToString(CultureInfo.InvariantCulture),
        EffectiveStatus(intent),
        intent.MatchedDepositId,
        intent.CreatedAt);

    /// <summary>"pending" | "confirmed" | "expired" | "failed" — a lapsed-but-not-yet-swept invoice already
    /// reads as expired, matching what a payer/merchant/Ops should all see.</summary>
    private string EffectiveStatus(PaymentIntentEntity intent) => intent.Status switch
    {
        PaymentIntentStatus.Matched => "confirmed",
        PaymentIntentStatus.Expired => "expired",
        PaymentIntentStatus.Failed => "failed",
        _ => timeProvider.GetUtcNow() >= intent.ExpiresAt ? "expired" : "pending",
    };
}
