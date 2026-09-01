using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when company funds moved into a hot withdrawal wallet have been recorded and verified on-chain.
/// The Ledger consumes it to book <c>Dr TreasuryAsset / Cr WithdrawalWalletTopUp</c> — custody genuinely rose,
/// and the counterparty is a System-owned contribution account, never a merchant's.
///
/// <para>It travels via the outbox like every other ledger-affecting event in this module, so the posting is
/// durable and idempotent rather than a best-effort call made inline with the write. Amounts are exact
/// base-unit integer strings (§14). <see cref="TopUpId"/> is the ledger's idempotency key.</para>
/// </summary>
public sealed record HotWalletToppedUp(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid TopUpId,
    Guid AssetId,
    Guid TargetWalletId,
    string TargetAddress,
    string AmountBaseUnits,
    string TransactionHash,
    DateTimeOffset RecordedAt) : IDomainEvent, IIntegrationEvent;
