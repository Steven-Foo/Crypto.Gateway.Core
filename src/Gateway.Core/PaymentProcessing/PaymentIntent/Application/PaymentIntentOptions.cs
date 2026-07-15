namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;

/// <summary>How long an unpaid invoice holds its address, and how hard the service tries to reserve one.</summary>
public sealed class PaymentIntentOptions
{
    public const string SectionName = "PaymentIntent";

    /// <summary>Minutes a Waiting invoice stays live before it expires and frees its address (mirrors the reference gateway's 30).</summary>
    public int ExpiryMinutes { get; init; } = 30;

    /// <summary>Bounded retries when a reused address is grabbed concurrently; each retry mints a fresh address.</summary>
    public int MaxProvisionRetries { get; init; } = 5;
}
