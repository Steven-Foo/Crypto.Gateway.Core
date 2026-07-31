using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;

/// <summary>
/// One reference's callback delivery state for Ops. <see cref="Status"/> is one of
/// <c>"PendingNotification"</c> (still retrying, or awaiting its first attempt), <c>"Notified"</c>,
/// <c>"Abandoned"</c> (automatic retries exhausted — see <c>CallbackDeliveryProcessingBackoff</c> — but
/// still resendable manually), or <c>null</c> if no delivery was ever scheduled (e.g. the transaction
/// hasn't reached a callback-worthy state yet, or the merchant set no callback URL — in that case
/// <see cref="AttemptCount"/> is 0 and <see cref="NextAttemptAt"/> is null too).
/// <see cref="AttemptCount"/> counts failed automatic attempts only (a successful attempt doesn't
/// increment it); <see cref="NextAttemptAt"/> is exactly when the worker will try next — null once
/// terminal (<c>Notified</c>/<c>Abandoned</c>), so a frontend never has to recompute a countdown from the
/// backoff schedule itself.
/// </summary>
public sealed record CallbackDeliveryStatusView(string? Status, int AttemptCount, DateTimeOffset? NextAttemptAt);

/// <summary>The Ops-facing read side of callback delivery.</summary>
public interface ICallbackDeliveryQuery
{
    Task<IReadOnlyDictionary<Guid, CallbackDeliveryStatusView>> GetStatusesAsync(
        CallbackReferenceType referenceType, IReadOnlyCollection<Guid> referenceIds, CancellationToken cancellationToken = default);
}
