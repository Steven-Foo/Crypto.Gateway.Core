namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;

/// <summary>
/// The lifecycle of a deposit invoice. A <see cref="Waiting"/> intent is holding its address; it becomes
/// <see cref="Matched"/> when a confirmed on-chain deposit lands, or <see cref="Expired"/> if it times out
/// unpaid. Matched/Expired both free the address for the merchant's next invoice.
/// </summary>
public enum PaymentIntentStatus
{
    Waiting = 1,
    Matched = 2,
    Expired = 3,
}
