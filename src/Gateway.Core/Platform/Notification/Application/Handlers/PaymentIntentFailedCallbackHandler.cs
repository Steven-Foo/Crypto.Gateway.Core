using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;

/// <summary>
/// Tells the merchant their invoice was manually cancelled by staff. Same envelope/signing scheme as
/// <see cref="DepositCallbackHandler"/> (frozen contract, §11) — only the status and payload shrink, since
/// no deposit ever arrived for a failed invoice. Delivered off the PaymentIntent outbox: durable,
/// at-least-once, retried by the dispatcher on a non-2xx response.
/// </summary>
public sealed class PaymentIntentFailedCallbackHandler(
    IMerchantCallbackSigner signer,
    IWebhookSender sender,
    ILogger<PaymentIntentFailedCallbackHandler> logger) : IIntegrationEventHandler<PaymentIntentFailed>
{
    private const string CallbackType = "crypto-transaction";

    public async Task HandleAsync(PaymentIntentFailed @event, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(@event.CallbackUrl))
            return; // the merchant did not ask for a callback

        var body = BuildPayload(@event);

        var signature = await signer.SignAsync(@event.MerchantId, body, cancellationToken);
        if (signature.IsFailure)
        {
            logger.LogWarning("No signing credential for merchant {MerchantId}; fail callback skipped.", @event.MerchantId);
            return;
        }

        var delivered = await sender.SendAsync(
            @event.CallbackUrl!, body, CallbackType, signature.Value.Timestamp, signature.Value.SignatureHex, cancellationToken);

        if (!delivered)
            throw new DomainException($"Fail callback to the merchant endpoint was not accepted; will retry (ref {@event.PublicReference}).");

        logger.LogInformation("Fail callback delivered for {Reference}.", @event.PublicReference);
    }

    private static string BuildPayload(PaymentIntentFailed e) =>
        JsonSerializer.Serialize(new
        {
            transactionId = e.MerchantTransactionId,
            data = new
            {
                transactionId = e.MerchantTransactionId,
                referenceNo = e.PublicReference,
                type = "deposit",
                status = "failed",
                reason = e.Reason,
                timestamp = e.FailedAt,
            },
        });
}
