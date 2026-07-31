using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;

/// <summary>
/// Builds and signs the merchant's deposit callback when an invoice is matched, then hands it to
/// <see cref="ICallbackDeliveryScheduler"/> — it never sends and never retries itself. Actual delivery
/// (with a bounded backoff schedule, not the Outbox's retry-forever) is
/// <c>CallbackDeliveryProcessingService</c>'s job. Scheduling is idempotent, so a redelivered
/// <c>PaymentIntentMatched</c> (the outbox is at-least-once) is a no-op if already scheduled.
///
/// <para>Amounts are converted to display decimals at this boundary (§14). Deposit chain details the event
/// does not carry yet (fromAddress, block, confirmations, gas) are omitted — a documented enrichment.</para>
/// </summary>
public sealed class DepositCallbackHandler(
    IMerchantCallbackSigner signer,
    IAssetCatalog assets,
    ICallbackDeliveryScheduler scheduler,
    ILogger<DepositCallbackHandler> logger) : IIntegrationEventHandler<PaymentIntentMatched>
{
    private const string CallbackType = "crypto-transaction";

    public async Task HandleAsync(PaymentIntentMatched @event, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(@event.CallbackUrl))
            return; // the merchant did not ask for a callback

        var asset = await assets.FindByIdAsync(@event.AssetId, cancellationToken);
        var body = BuildPayload(@event, asset?.Symbol ?? "USDT", asset?.Decimals ?? 6);

        var signature = await signer.SignAsync(@event.MerchantId, body, cancellationToken);
        if (signature.IsFailure)
        {
            // No active signing credential — we cannot authenticate the callback, so we do not schedule an
            // unsigned one. Not retryable; log and drop.
            logger.LogWarning("No signing credential for merchant {MerchantId}; deposit callback skipped.", @event.MerchantId);
            return;
        }

        await scheduler.ScheduleAsync(
            CallbackReferenceType.Deposit, @event.PublicReference,
            @event.CallbackUrl!, body, CallbackType, signature.Value.Timestamp, signature.Value.SignatureHex,
            cancellationToken);
    }

    private static string BuildPayload(PaymentIntentMatched e, string currencyCode, int decimals) =>
        JsonSerializer.Serialize(new
        {
            transactionId = e.MerchantTransactionId,
            data = new
            {
                transactionId = e.MerchantTransactionId,
                referenceNo = e.PublicReference,
                txHash = e.TransactionHash,
                type = "deposit",
                toAddress = e.Address,
                amount = ToDisplay(e.ActualAmountBaseUnits, decimals),
                currencyCode,
                status = "confirmed",
                expectedAmount = ToDisplay(e.ExpectedAmountBaseUnits, decimals),
                amountMatched = e.AmountMatched,
                timestamp = e.MatchedAt,
            },
        });

    private static decimal ToDisplay(string baseUnits, int decimals)
    {
        var value = BigInteger.Parse(baseUnits, CultureInfo.InvariantCulture);
        var factor = 1m;
        for (var i = 0; i < decimals; i++)
            factor *= 10m;
        return (decimal)value / factor;
    }
}
