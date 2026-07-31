using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;

/// <summary>
/// Schedules a callback for delivery. Called once by the handler that reacts to the source event (e.g.
/// <c>DepositCallbackHandler</c>) — it persists the already-signed request and hands actual sending off to
/// the <c>CallbackDeliveryProcessingService</c> worker; the caller never sends and never retries itself.
/// Idempotent: a redelivered source event (the outbox is at-least-once) is a no-op if already scheduled.
/// </summary>
public interface ICallbackDeliveryScheduler
{
    Task ScheduleAsync(
        CallbackReferenceType referenceType,
        Guid referenceId,
        string callbackUrl,
        string body,
        string callbackType,
        string timestamp,
        string signatureHex,
        CancellationToken cancellationToken = default);
}
