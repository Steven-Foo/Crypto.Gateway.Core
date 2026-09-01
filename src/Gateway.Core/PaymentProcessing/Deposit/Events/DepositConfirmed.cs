using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Events;

/// <summary>
/// Published by the Deposit module once an on-chain deposit has reached the required confirmations.
/// The Ledger consumes this to credit the merchant. The publisher owns this contract; consumers
/// reference this Events project only (§4.5, §7.5).
///
/// <para><b>Money on the wire:</b> <see cref="AmountBaseUnits"/> is the exact unsigned integer amount
/// in the asset's base units, carried as a string so no serializer can silently narrow it (§14).
/// <see cref="FeeBaseUnits"/> is the platform deposit fee snapshotted by the Deposit module at detection,
/// carried the same way — the Ledger books the split from this exact value rather than re-deriving it, so
/// the fee it charges can never drift from the fee the deposit was priced at (Withdrawal-symmetric). A
/// pre-fee event (in-flight before this field existed) deserializes it as null ⇒ the Ledger treats it as
/// zero, collapsing to the original no-fee journal.</para>
///
/// <para><b><see cref="Kind"/></b> is "Customer" or "MerchantTopUp" — a merchant paying itself in, versus a
/// customer paying the merchant. Carried as a <em>string</em>, not the publisher's enum, so a consumer
/// depends on the shape and not on Deposit's domain type (§4.5, and Kafka-ready by contract §7.5). It
/// decides which <c>JournalReferenceType</c> the Ledger posts under, which in turn is what exempts a top-up
/// from the merchant's T+N settlement hold. Null on an event in flight from before this field existed ⇒
/// treated as "Customer", the correct reading of every deposit that predates top-up.</para>
/// </summary>
public sealed record DepositConfirmed(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid DepositId,
    Guid WalletId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    string FeeBaseUnits,
    Chain Chain,
    string TransactionHash,
    int OutputIndex,
    DateTimeOffset ConfirmedAt,
    string? Kind = null) : IDomainEvent, IIntegrationEvent;
