using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Handlers;

/// <summary>
/// Tells the merchant a withdrawal was rejected (approval denied) or failed before broadcast — same
/// envelope/signing scheme and the same schedule-don't-send split as
/// <see cref="WithdrawalConfirmedCallbackHandler"/>/<see cref="PaymentIntentFailedCallbackHandler"/>. Never
/// raised after broadcast (§ Withdrawal.Fail) — funds may be on-chain by then, so this only ever reports a
/// withdrawal that genuinely never left custody.
/// </summary>
public sealed class WithdrawalFailedCallbackHandler(
    IMerchantCallbackSigner signer,
    ICallbackDeliveryScheduler scheduler,
    ILogger<WithdrawalFailedCallbackHandler> logger) : IIntegrationEventHandler<WithdrawalFailed>
{
    private const string CallbackType = "crypto-transaction";

    public async Task HandleAsync(WithdrawalFailed @event, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(@event.CallbackUrl))
            return; // the merchant did not ask for a callback

        var body = BuildPayload(@event);

        var signature = await signer.SignAsync(@event.MerchantId, body, cancellationToken);
        if (signature.IsFailure)
        {
            logger.LogWarning("No signing credential for merchant {MerchantId}; withdrawal fail callback skipped.", @event.MerchantId);
            return;
        }

        await scheduler.ScheduleAsync(
            CallbackReferenceType.Withdrawal, @event.WithdrawalId,
            @event.CallbackUrl!, body, CallbackType, signature.Value.Timestamp, signature.Value.SignatureHex,
            cancellationToken);
    }

    private static string BuildPayload(WithdrawalFailed e) =>
        JsonSerializer.Serialize(new
        {
            transactionId = e.MerchantTransactionId,
            data = new
            {
                transactionId = e.MerchantTransactionId,
                referenceNo = e.WithdrawalId,
                type = "withdraw",
                status = "failed",
                reason = e.Reason,
                timestamp = e.FailedAt,
            },
        });
}
