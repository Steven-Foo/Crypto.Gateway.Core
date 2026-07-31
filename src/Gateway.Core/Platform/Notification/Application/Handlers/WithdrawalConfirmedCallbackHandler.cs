using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;

/// <summary>
/// Builds and signs the merchant's withdrawal callback once a withdrawal confirms on-chain, then hands it
/// to <see cref="ICallbackDeliveryScheduler"/> — the exact same split as <see cref="DepositCallbackHandler"/>:
/// this handler never sends and never retries itself, and gets the same bounded backoff/abandon/manual-resend
/// behavior for free via <c>CallbackDeliveryProcessingService</c>. <c>type</c> in the payload is
/// <c>"withdraw"</c>, matching the frozen partner vocabulary already used by
/// <c>/api/v1/transactions/query</c>, not the internal <see cref="CallbackReferenceType.Withdrawal"/> name.
/// </summary>
public sealed class WithdrawalConfirmedCallbackHandler(
    IMerchantCallbackSigner signer,
    IAssetCatalog assets,
    ICallbackDeliveryScheduler scheduler,
    ILogger<WithdrawalConfirmedCallbackHandler> logger) : IIntegrationEventHandler<WithdrawalConfirmed>
{
    private const string CallbackType = "crypto-transaction";

    public async Task HandleAsync(WithdrawalConfirmed @event, CancellationToken cancellationToken = default)
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
            logger.LogWarning("No signing credential for merchant {MerchantId}; withdrawal callback skipped.", @event.MerchantId);
            return;
        }

        await scheduler.ScheduleAsync(
            CallbackReferenceType.Withdrawal, @event.WithdrawalId,
            @event.CallbackUrl!, body, CallbackType, signature.Value.Timestamp, signature.Value.SignatureHex,
            cancellationToken);
    }

    private static string BuildPayload(WithdrawalConfirmed e, string currencyCode, int decimals) =>
        JsonSerializer.Serialize(new
        {
            transactionId = e.IdempotencyKey,
            data = new
            {
                transactionId = e.IdempotencyKey,
                referenceNo = e.WithdrawalId,
                txHash = e.TransactionHash,
                type = "withdraw",
                toAddress = e.DestinationAddress,
                amount = ToDisplay(e.AmountBaseUnits, decimals),
                currencyCode,
                status = "confirmed",
                timestamp = e.ConfirmedAt,
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
