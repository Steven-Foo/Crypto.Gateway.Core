using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when a withdrawal is rejected (approval denied) or fails before broadcast (no funds left
/// the platform). The Ledger consumes it to <b>release</b> the reserved funds back to the merchant.
/// Never raised after broadcast — once funds may be on-chain, a stuck withdrawal is an ops incident,
/// not an automatic release. <see cref="IdempotencyKey"/>/<see cref="CallbackUrl"/> exist so
/// Notification's withdrawal callback handler can build the merchant payload without looking anything up
/// (§4.5, mirrors <c>PaymentIntentFailed</c>).
/// </summary>
public sealed record WithdrawalFailed(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid WithdrawalId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string Reason,
    DateTimeOffset FailedAt,
    string IdempotencyKey,
    string? CallbackUrl) : IDomainEvent, IIntegrationEvent;
