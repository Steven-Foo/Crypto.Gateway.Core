using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;

/// <summary>
/// The resumable scan watermark per chain (§9): the last block fully scanned for deposits. Persisted,
/// so a restart resumes exactly where it left off and never rescans from genesis nor skips a block.
/// </summary>
public interface IScanCursorStore
{
    Task<long> GetLastScannedBlockAsync(Chain chain, CancellationToken cancellationToken = default);

    Task SetLastScannedBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default);
}
