using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

/// <summary>
/// Persistence-only watermark: the last block fully scanned per chain. Not a domain aggregate — it
/// carries no business rule, just where the scanner resumes — so it lives in Infrastructure.
/// </summary>
public sealed class ScanCursor
{
    public Chain Chain { get; private set; }
    public long LastScannedBlock { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ScanCursor()
    {
    }

    public ScanCursor(Chain chain, long lastScannedBlock, DateTimeOffset updatedAt)
    {
        Chain = chain;
        LastScannedBlock = lastScannedBlock;
        UpdatedAt = updatedAt;
    }

    public void Advance(long blockNumber, DateTimeOffset now)
    {
        if (blockNumber > LastScannedBlock)
        {
            LastScannedBlock = blockNumber;
            UpdatedAt = now;
        }
    }
}
