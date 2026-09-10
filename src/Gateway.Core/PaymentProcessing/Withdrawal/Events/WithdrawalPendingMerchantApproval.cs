using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Events;

/// <summary>
/// Published when a portal-initiated payout comes to rest awaiting the MERCHANT's own approver. It tells the
/// merchant "one of your users submitted a payout and it needs sign-off" — without it the only way to notice
/// was to open the portal and look, so a payout could sit unapproved indefinitely simply because nobody
/// thought to check.
///
/// <para><b>Raised for portal-initiated payouts only.</b> An HMAC-API payout never enters this state (the
/// merchant's server already authorised it by signing the request), so no callback fires for one — the frozen
/// API behaviour is unchanged.</para>
///
/// <para>This event moves <b>no money and has no ledger impact</b>: the reserve already happened at request
/// time, and the payout's position in the state machine is unchanged by anyone being told about it. It is
/// purely an outbound notification trigger, which is why a delivery failure can never affect correctness —
/// the payout still sits safely awaiting approval either way.</para>
///
/// <para>Carries <see cref="MerchantTransactionId"/>/<see cref="CallbackUrl"/> so Notification can build the
/// merchant payload without looking anything up (§4.5, mirroring <c>WithdrawalFailed</c>).</para>
/// </summary>
public sealed record WithdrawalPendingMerchantApproval(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    Guid WithdrawalId,
    Guid MerchantId,
    Guid AssetId,
    string AmountBaseUnits,
    string FeeBaseUnits,
    string ReceivingAddress,
    DateTimeOffset RequestedAt,
    string MerchantTransactionId,
    string? CallbackUrl) : IDomainEvent, IIntegrationEvent;
