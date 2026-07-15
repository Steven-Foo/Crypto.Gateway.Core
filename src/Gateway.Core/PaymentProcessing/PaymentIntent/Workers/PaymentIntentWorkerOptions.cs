namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Workers;

/// <summary>Cadence and batch size for the expiry sweep that frees lapsed invoices' addresses for reuse.</summary>
public sealed class PaymentIntentWorkerOptions
{
    public TimeSpan ExpirySweepInterval { get; init; } = TimeSpan.FromMinutes(1);

    public int ExpiryBatchSize { get; init; } = 200;
}
