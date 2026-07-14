using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when a withdrawal has confirmed on-chain. The Ledger consumes it to <b>settle</b> — move
/// the amount out of custody and book the fee as revenue. Amounts are exact base-unit integer strings
/// (§14). The publisher (Withdrawal) owns this contract; consumers reference this Events project (§4.5).
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
    DateTimeOffset ConfirmedAt) : IDomainEvent, IIntegrationEvent;
