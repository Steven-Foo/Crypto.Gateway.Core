namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;

/// <summary>
/// The lifecycle of a deposit invoice. A <see cref="Waiting"/> intent is holding its address; it becomes
/// <see cref="Matched"/> when a confirmed on-chain deposit lands, <see cref="Expired"/> if it times out
/// unpaid, or <see cref="Failed"/> if staff manually cancel it (e.g. a test invoice). All three terminal
/// states free the address for the merchant's next invoice. <see cref="Failed"/> is only reachable from
/// <see cref="Waiting"/> — no money has moved for an unmatched invoice, so there is nothing to reverse.
/// </summary>
public enum PaymentIntentStatus
{
    Waiting = 1,
    Matched = 2,
    Expired = 3,
    Failed = 4,
}
