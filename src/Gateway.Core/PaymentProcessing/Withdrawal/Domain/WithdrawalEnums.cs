namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

/// <summary>
/// The withdrawal lifecycle. Funds are reserved in the ledger the moment a withdrawal is created;
/// they leave custody only at <see cref="Confirmed"/>, and return to the merchant on
/// <see cref="Rejected"/>/<see cref="Failed"/> (both pre-broadcast, so nothing left the chain).
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
}
