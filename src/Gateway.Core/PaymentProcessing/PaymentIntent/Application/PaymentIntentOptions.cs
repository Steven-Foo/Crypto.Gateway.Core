namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;

/// <summary>How long an unpaid invoice holds its address, and how hard the service tries to reserve one.</summary>
public sealed class PaymentIntentOptions
{
    public const string SectionName = "PaymentIntent";

    /// <summary>Minutes until the pay page/countdown shows the invoice as expired to the payer (mirrors the reference gateway's 30).</summary>
    public int ExpiryMinutes { get; init; } = 30;

    /// <summary>
    /// Extra minutes AFTER <see cref="ExpiryMinutes"/> during which the invoice's wallet stays reserved and
    /// still matchable — the payer sees "expired" already, but a transfer broadcast just before the deadline
    /// (and only confirming slightly after) is still captured instead of silently lost. Only once
    /// <c>ExpiryMinutes + GraceMinutes</c> has fully elapsed does the address actually free up for reuse
    /// (mirrors the reference gateway's 10-minute grace window).
    /// </summary>
    public int GraceMinutes { get; init; } = 10;

    /// <summary>Bounded retries when a reused address is grabbed concurrently; each retry mints a fresh address.</summary>
    public int MaxProvisionRetries { get; init; } = 5;
}
