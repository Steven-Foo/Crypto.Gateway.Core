using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;

/// <summary>
/// Delivers the merchant's deposit callback when an invoice is matched. Builds the frozen payload, signs it
/// with the merchant's key (via <see cref="IMerchantCallbackSigner"/> — the secret never leaves the Merchant
/// module), and POSTs it. Delivered off the PaymentIntent outbox, so it is durable and at-least-once: a
/// non-2xx response throws, leaving the message for the dispatcher to retry. Idempotent for the merchant
/// because the payload carries their transaction reference.
///
/// <para>Amounts are converted to display decimals at this boundary (§14). Deposit chain details the event
/// does not carry yet (fromAddress, block, confirmations, gas) are omitted — a documented enrichment.</para>
/// </summary>
public sealed class DepositCallbackHandler(
    IMerchantCallbackSigner signer,
    IAssetCatalog assets,
    IWebhookSender sender,
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
            // No active signing credential — we cannot authenticate the callback, so we do not send an
            // unsigned one. Not retryable; log and drop.
            logger.LogWarning("No signing credential for merchant {MerchantId}; deposit callback skipped.", @event.MerchantId);
            return;
        }

        var delivered = await sender.SendAsync(
            @event.CallbackUrl!, body, CallbackType, signature.Value.Timestamp, signature.Value.SignatureHex, cancellationToken);

        if (!delivered)
            throw new DomainException($"Deposit callback to the merchant endpoint was not accepted; will retry (ref {@event.PublicReference}).");

        logger.LogInformation("Deposit callback delivered for {Reference}.", @event.PublicReference);
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
