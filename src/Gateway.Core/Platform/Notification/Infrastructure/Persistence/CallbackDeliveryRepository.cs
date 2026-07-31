using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Infrastructure.Persistence;

/// <summary>Backs both scheduling (<see cref="ICallbackDeliveryScheduler"/>) and the worker/resend
/// repository (<see cref="ICallbackDeliveryRepository"/>) — one table, one class, matching how
/// <c>WithdrawalRepository</c> owns both writes for its module.</summary>
public sealed class CallbackDeliveryRepository(NotificationDbContext context) : ICallbackDeliveryScheduler, ICallbackDeliveryRepository
{
    public async Task ScheduleAsync(
        CallbackReferenceType referenceType,
        Guid referenceId,
        string callbackUrl,
        string body,
        string callbackType,
        string timestamp,
        string signatureHex,
        CancellationToken cancellationToken = default)
    {
        var delivery = CallbackDelivery.Schedule(
            referenceType, referenceId, callbackUrl, body, callbackType, timestamp, signatureHex, DateTimeOffset.UtcNow);

        await AddIfNewAsync(delivery, cancellationToken);
    }

    public Task<CallbackDelivery?> FindAsync(
        CallbackReferenceType referenceType, Guid referenceId, CancellationToken cancellationToken = default) =>
        context.CallbackDeliveries.SingleOrDefaultAsync(
            c => c.ReferenceType == referenceType && c.ReferenceId == referenceId, cancellationToken);

    public async Task<IReadOnlyList<CallbackDelivery>> GetDueAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default) =>
        await context.CallbackDeliveries
            .Where(c => c.Status == CallbackDeliveryStatus.Pending && c.NextAttemptAt <= asOf)
            .OrderBy(c => c.NextAttemptAt)
            .ToListAsync(cancellationToken);

    public async Task<CallbackDeliveryRecordOutcome> AddIfNewAsync(CallbackDelivery delivery, CancellationToken cancellationToken = default)
    {
        context.CallbackDeliveries.Add(delivery);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return CallbackDeliveryRecordOutcome.Recorded;
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            context.Entry(delivery).State = EntityState.Detached;
            return CallbackDeliveryRecordOutcome.Duplicate;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
