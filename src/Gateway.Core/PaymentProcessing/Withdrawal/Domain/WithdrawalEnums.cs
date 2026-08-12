namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

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
}
