using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;

public enum CallbackDeliveryRecordOutcome
{
    Recorded,
    Duplicate,
}

/// <summary>Persistence for the delivery worker and the manual-resend service.</summary>
public interface ICallbackDeliveryRepository
{
    Task<CallbackDelivery?> FindAsync(
        CallbackReferenceType referenceType, Guid referenceId, CancellationToken cancellationToken = default);

    /// <summary>Rows due for another automatic attempt right now, oldest first.</summary>
    Task<IReadOnlyList<CallbackDelivery>> GetDueAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default);

    /// <summary>Idempotent insert — a redelivered source event that already scheduled this reference is a no-op.</summary>
    Task<CallbackDeliveryRecordOutcome> AddIfNewAsync(CallbackDelivery delivery, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
