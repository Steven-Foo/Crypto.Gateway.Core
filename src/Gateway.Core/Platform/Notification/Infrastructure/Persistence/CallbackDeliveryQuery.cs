using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure.Persistence;

public sealed class CallbackDeliveryQuery(NotificationDbContext context) : ICallbackDeliveryQuery
{
    private static readonly CallbackDeliveryStatusView NeverScheduled = new(null, 0, null);

    public async Task<IReadOnlyDictionary<Guid, CallbackDeliveryStatusView>> GetStatusesAsync(
        CallbackReferenceType referenceType, IReadOnlyCollection<Guid> referenceIds, CancellationToken cancellationToken = default)
    {
        if (referenceIds.Count == 0)
            return new Dictionary<Guid, CallbackDeliveryStatusView>();

        var rows = await context.CallbackDeliveries.AsNoTracking()
            .Where(c => c.ReferenceType == referenceType && referenceIds.Contains(c.ReferenceId))
            .Select(c => new { c.ReferenceId, c.Status, c.AttemptCount, c.NextAttemptAt })
            .ToListAsync(cancellationToken);

        var byId = rows.ToDictionary(
            r => r.ReferenceId,
            r => new CallbackDeliveryStatusView(ToApiStatus(r.Status), r.AttemptCount, r.NextAttemptAt));

        return referenceIds.ToDictionary(id => id, id => byId.GetValueOrDefault(id, NeverScheduled));
    }

    private static string ToApiStatus(CallbackDeliveryStatus status) => status switch
    {
        CallbackDeliveryStatus.Pending => "PendingNotification",
        CallbackDeliveryStatus.Notified => "Notified",
        CallbackDeliveryStatus.Abandoned => "Abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
