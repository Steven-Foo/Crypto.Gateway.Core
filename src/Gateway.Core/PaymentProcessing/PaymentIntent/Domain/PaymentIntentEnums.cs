namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;

/// <summary>
/// The lifecycle of a deposit invoice. A <see cref="Waiting"/> intent is holding its address; it becomes
/// <see cref="Matched"/> the moment any confirmed on-chain deposit lands (exact amount or not — see
/// <c>PaymentIntent.MatchTo</c>), <see cref="Expired"/> if it times out unpaid, or <see cref="Failed"/> if
/// staff manually cancel it (e.g. a test invoice). All terminal states free the address for the merchant's
/// next invoice.
/// </summary>
public enum PaymentIntentStatus
{
    Waiting = 1,
    Matched = 2,
    Expired = 3,
    Failed = 4,
}
