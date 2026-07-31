using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;

/// <summary>
/// Drives due callback deliveries through the bounded retry schedule (§ CallbackDeliveryProcessingBackoff):
/// 30s, 1m, 2m, 4m, 10m after the immediate first attempt, then <see cref="CallbackDeliveryStatus.Abandoned"/>.
/// This replaces the generic Outbox's retry-forever behavior for merchant callbacks specifically — Outbox
/// dispatch itself still retries forever for everything else (correctness-critical events like ledger
/// postings must never give up), only callback delivery has a bounded, backed-off give-up point.
/// </summary>
public sealed class CallbackDeliveryProcessingService(
    ICallbackDeliveryRepository repository,
    IWebhookSender sender,
    TimeProvider timeProvider,
    ILogger<CallbackDeliveryProcessingService> logger)
{
    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var due = await repository.GetDueAsync(timeProvider.GetUtcNow(), cancellationToken);
        if (due.Count == 0)
            return 0;

        foreach (var delivery in due)
            await ProcessOneAsync(delivery, cancellationToken);

        await repository.SaveChangesAsync(cancellationToken);
        return due.Count;
    }

    private async Task ProcessOneAsync(CallbackDelivery delivery, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        bool delivered;
        try
        {
            delivered = await sender.SendAsync(
                delivery.CallbackUrl, delivery.Body, delivery.CallbackType, delivery.Timestamp, delivery.SignatureHex, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // IWebhookSender already turns transport errors into `false` — this only catches something
            // unexpected. Treat it exactly like a non-2xx: a failed attempt against the retry schedule.
            logger.LogError(ex, "Unexpected error sending callback for {ReferenceType}:{ReferenceId}.", delivery.ReferenceType, delivery.ReferenceId);
            delivery.RecordFailure(ex.Message, now, CallbackDeliveryProcessingBackoff.Schedule);
            return;
        }

        if (delivered)
        {
            delivery.RecordSuccess(now);
            logger.LogInformation("Callback delivered for {ReferenceType}:{ReferenceId}.", delivery.ReferenceType, delivery.ReferenceId);
            return;
        }

        delivery.RecordFailure("Merchant endpoint did not return a 2xx response.", now, CallbackDeliveryProcessingBackoff.Schedule);

        if (delivery.Status == CallbackDeliveryStatus.Abandoned)
            logger.LogWarning(
                "Callback for {ReferenceType}:{ReferenceId} abandoned after {Attempts} attempts.",
                delivery.ReferenceType, delivery.ReferenceId, delivery.AttemptCount);
        else
            logger.LogWarning(
                "Callback attempt {Attempt} failed for {ReferenceType}:{ReferenceId}; next attempt at {NextAttemptAt}.",
                delivery.AttemptCount, delivery.ReferenceType, delivery.ReferenceId, delivery.NextAttemptAt);
    }
}
