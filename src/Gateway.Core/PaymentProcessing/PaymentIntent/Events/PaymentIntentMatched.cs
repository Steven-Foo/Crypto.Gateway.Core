using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Events;

/// <summary>
/// Published when a confirmed deposit is matched to a merchant's invoice. It carries everything a merchant
/// callback needs — the invoice side (their reference, expected amount, callback url) and the deposit side
/// (tx hash, actual amount) — so a consumer builds the notification without reaching into either module.
///
/// <para><b>Money on the wire:</b> amounts are exact base-unit integer strings (§14). Delivered via the
/// PaymentIntent outbox, so it is durable and at-least-once — a callback consumer must be idempotent.</para>
/// </summary>
public sealed record PaymentIntentMatched(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid MerchantId,
    Guid PublicReference,
    string MerchantTransactionId,
    string? CallbackUrl,
    Chain Chain,
    Guid AssetId,
    string Address,
    string ExpectedAmountBaseUnits,
    string ActualAmountBaseUnits,
    bool AmountMatched,
    Guid DepositId,
    string TransactionHash,
    DateTimeOffset MatchedAt) : IDomainEvent, IIntegrationEvent;
