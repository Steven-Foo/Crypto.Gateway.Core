using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;

/// <summary>
/// Published by the Deposit module when a previously-confirmed deposit is orphaned by a chain reorg.
/// The Ledger consumes this to post a <em>compensating</em> journal — it never edits the original
/// credit (§9, §14). <see cref="AmountBaseUnits"/> is the exact base-unit amount that was credited.
/// </summary>
public sealed record DepositOrphaned(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid DepositId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    Chain Chain,
    string TransactionHash,
    int OutputIndex,
    DateTimeOffset OrphanedAt) : IDomainEvent, IIntegrationEvent;
