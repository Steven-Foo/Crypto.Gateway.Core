using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;

public interface ICallbackDeliveryResendService
{
    /// <summary>Ops-triggered: resends the exact persisted, already-signed payload for a callback that
    /// automatic retries gave up on. Only valid once the delivery is <c>Abandoned</c>.</summary>
    Task<Result> ResendAsync(
        CallbackReferenceType referenceType, Guid referenceId, CancellationToken cancellationToken = default);
}

/// <summary>The staff-only counterpart to the automatic retry worker — same send path, same persisted
/// payload, but a one-off synchronous attempt an operator explicitly asked for.</summary>
public sealed class CallbackDeliveryResendService(
    ICallbackDeliveryRepository repository,
    IWebhookSender sender,
    TimeProvider timeProvider,
    ILogger<CallbackDeliveryResendService> logger) : ICallbackDeliveryResendService
{
    public async Task<Result> ResendAsync(
        CallbackReferenceType referenceType, Guid referenceId, CancellationToken cancellationToken = default)
    {
        var delivery = await repository.FindAsync(referenceType, referenceId, cancellationToken);
        if (delivery is null)
            return Result.Failure(CallbackDeliveryErrors.NotFound);

        // Gate BEFORE sending, not just before recording — a premature call (still Pending, mid-backoff)
        // must genuinely do nothing, not fire a real webhook and then merely fail to record it.
        if (delivery.Status != CallbackDeliveryStatus.Abandoned)
            return Result.Failure(CallbackDeliveryErrors.NotAbandoned);

        var delivered = await sender.SendAsync(
            delivery.CallbackUrl, delivery.Body, delivery.CallbackType, delivery.Timestamp, delivery.SignatureHex, cancellationToken);

        var result = delivery.RecordManualResendOutcome(
            delivered, delivered ? null : "Merchant endpoint did not return a 2xx response.", timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Manual callback resend for {ReferenceType}:{ReferenceId}: {Outcome}.",
            referenceType, referenceId, delivered ? "delivered" : "still failing");
        return Result.Success();
    }
}
