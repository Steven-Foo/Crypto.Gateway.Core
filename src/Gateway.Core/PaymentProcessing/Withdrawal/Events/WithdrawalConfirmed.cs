using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when a withdrawal has confirmed on-chain. The Ledger consumes it to <b>settle</b> — move
/// the amount out of custody and book the fee as revenue. Amounts are exact base-unit integer strings
/// (§14). <see cref="IdempotencyKey"/>/<see cref="DestinationAddress"/>/<see cref="CallbackUrl"/> exist so
/// Notification's withdrawal callback handler can build the merchant payload without looking anything up
/// (§4.5, mirrors <c>PaymentIntentMatched</c>). The publisher (Withdrawal) owns this contract; consumers
/// reference this Events project.
/// </summary>
public sealed record WithdrawalConfirmed(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid WithdrawalId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string TransactionHash,
    DateTimeOffset ConfirmedAt,
    string IdempotencyKey,
    string DestinationAddress,
    string? CallbackUrl) : IDomainEvent, IIntegrationEvent;
