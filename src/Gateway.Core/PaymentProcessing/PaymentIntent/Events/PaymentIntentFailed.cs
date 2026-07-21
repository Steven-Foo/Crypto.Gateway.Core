using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;

/// <summary>
/// Published when staff manually fail a still-<c>Waiting</c> invoice (e.g. a test transaction). No money
/// ever moved for this invoice, so unlike a deposit reorg there is nothing to reverse in the Ledger — this
/// is a notification-only event, consumed by the Notification module to tell the merchant their invoice
/// will not be fulfilled. Delivered via the PaymentIntent outbox (at-least-once; a consumer must be
/// idempotent).
/// </summary>
public sealed record PaymentIntentFailed(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid MerchantId,
    Guid PublicReference,
    string MerchantTransactionId,
    string? CallbackUrl,
    string Reason,
    DateTimeOffset FailedAt) : IDomainEvent, IIntegrationEvent;
