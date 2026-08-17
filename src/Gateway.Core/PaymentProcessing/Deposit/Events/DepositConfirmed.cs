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
    DateTimeOffset ConfirmedAt) : IDomainEvent, IIntegrationEvent;
