namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

/// <summary>
/// What kind of money-out this is. Both kinds share the identical execution pipeline (reserve → sign →
/// broadcast → confirm → settle) and ledger impact; they differ only in the <em>request</em> controls,
/// destination, and reporting — so they're one aggregate with a discriminator, never mixed.
/// </summary>
public enum WithdrawalKind
{
    /// <summary>A payout the merchant creates, typically for its own end-user. Gated by the per-transaction
    /// min/max withdrawal limits; destination is supplied per request.</summary>
    User = 0,

    /// <summary>The merchant cashing out its own accumulated earnings to its pre-registered settlement wallet.
    /// Gated by a flat/% liquidity cap; destination is the whitelisted settlement address, not client-supplied.</summary>
    Merchant = 1,
}

/// <summary>
/// The withdrawal lifecycle. Funds are reserved in the ledger the moment a withdrawal is created;
/// they leave custody only at <see cref="Confirmed"/>, and return to the merchant on
/// <see cref="Rejected"/>/<see cref="Failed"/> (both pre-broadcast, so nothing left the chain).
///
/// <para><see cref="AwaitingFunds"/> and <see cref="AwaitingRelease"/> are money-safety <em>holds</em>: the
/// merchant is genuinely owed the amount and the ledger reserve stays <b>held</b> (unlike <see cref="Failed"/>,
/// which releases). They exist because ledger sufficiency (the merchant can afford it) is independent of
/// physical sufficiency (the hot wallet actually holds enough on-chain to broadcast).</para>
/// </summary>
public enum WithdrawalStatus
{
    /// <summary>Created, but the ledger reserve has not yet completed. No funds locked until it does.</summary>
    Reserving = 0,

    /// <summary>Above the approval threshold — awaiting a human approver (§10).</summary>
    PendingApproval = 1,

    /// <summary>Cleared to process (auto below threshold, or manually approved).</summary>
    Approved = 2,

    /// <summary>An unsigned transaction has been handed to the signer.</summary>
    Signing = 3,

    /// <summary>A signed transaction has been broadcast to the chain.</summary>
    Broadcast = 4,

    /// <summary>Confirmed on-chain → the ledger settles.</summary>
    Confirmed = 5,

    /// <summary>Approval denied → reserved funds released.</summary>
    Rejected = 6,

    /// <summary>Failed before broadcast → reserved funds released.</summary>
    Failed = 7,

    /// <summary>
    /// The hot (withdrawal) wallet does not physically hold enough to broadcast this payout — the reserve is
    /// <b>held</b> (the merchant is still owed it), the reason is recorded for ops, and the withdrawal
    /// automatically re-evaluates each processing pass, resuming once the float is topped up. NOT a failure.
    /// </summary>
    AwaitingFunds = 8,

    /// <summary>
    /// The float is now sufficient, but the amount is above the approval threshold, so a human must release it
    /// to send (the "large = manual" resume rule). Reserve held; cleared by an ops release action.
    /// </summary>
    AwaitingRelease = 9,

    /// <summary>
    /// A portal-initiated payout awaiting the MERCHANT's own approval, before the platform ever looks at it.
    /// The merchant-side half of a two-party rule: one of their users submits it, one of their approvers signs
    /// it off. Reserve is already held (the merchant is committed to the amount); a merchant rejection releases
    /// it. Payouts created through the HMAC API skip this entirely — the merchant's own server already
    /// authorised those by signing the request.
    /// </summary>
    PendingMerchantApproval = 10,

    /// <summary>
    /// A MERCHANT settlement (cash-out) awaiting a platform admin's audit. Merchant settlements are not paid
    /// by this system: an operations/finance admin pays the merchant from a company wallet OUTSIDE platform
    /// custody, then records the transaction here. Reserve is held throughout — an audit rejection releases it.
    /// </summary>
    PendingAdminAudit = 11,

    /// <summary>
    /// Audit passed; the settlement is cleared for the finance admin to pay out externally. Distinct from
    /// <see cref="PendingAudit"/> so "reviewed" and "not yet paid" are never confused on the ops queue — they
    /// are usually different people. Reserve still held; the merchant's money moves only at
    /// <see cref="Completed"/>.
    /// </summary>
    PendingFinanceTransfer = 12,

    /// <summary>
    /// The finance admin paid the merchant from an external company wallet and recorded a transaction hash
    /// that was then verified on-chain. The ledger discharges the reserve against <c>ExternalSettlement</c> —
    /// platform custody is NOT reduced, because the funds never left an address this system watches.
    ///
    /// <para>Deliberately distinct from <see cref="Confirmed"/>, which means "this system built, signed,
    /// broadcast and confirmed the payment itself". The two have different origins of trust — one the platform
    /// performed, the other a human asserted and we verified — so an operator must be able to tell them apart
    /// at a glance.</para>
    /// </summary>
    FinanceSettled = 13,
}
