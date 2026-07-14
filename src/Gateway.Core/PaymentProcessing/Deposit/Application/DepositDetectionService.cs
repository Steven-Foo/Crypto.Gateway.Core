using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;

/// <summary>
/// Scans a chain for incoming transfers to watched addresses and records new deposits. It makes no
/// money decision beyond the dust floor — a deposit is only <em>detected</em> here; crediting waits for
/// confirmation (<see cref="DepositConfirmationService"/>). Resumable via the scan cursor and idempotent
/// via the deposit dedup key, so re-running a range is safe.
/// </summary>
public sealed class DepositDetectionService(
    IDepositScanner scanner,
    IChainStatusReader chainStatus,
    IWalletDirectory wallets,
    IDepositRepository repository,
    IScanCursorStore cursors,
    IDepositPolicyProvider policies,
    TimeProvider timeProvider,
    ILogger<DepositDetectionService> logger)
{
    /// <summary>An upper bound on how many blocks one pass consumes, so a cold start can't scan millions at once.</summary>
    private const int MaxBlocksPerScan = 500;

    /// <summary>Scans the next block window for one chain. Returns how many new deposits were recorded.</summary>
    public async Task<int> ScanOnceAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var tip = await chainStatus.GetTipHeightAsync(chain, cancellationToken);
        var lastScanned = await cursors.GetLastScannedBlockAsync(chain, cancellationToken);

        var fromBlock = lastScanned + 1;
        if (fromBlock > tip)
            return 0; // nothing new on-chain

        var toBlock = Math.Min(tip, fromBlock + MaxBlocksPerScan - 1);
        var transfers = await scanner.ScanAsync(chain, fromBlock, toBlock, cancellationToken);
        var policy = policies.For(chain);

        var recorded = 0;
        foreach (var transfer in transfers)
        {
            // "Whose address is this?" — a Contracts-only read into the Wallet module (§4.5).
            var owner = await wallets.FindByAddressAsync(transfer.Chain, transfer.Address, cancellationToken);
            if (owner is null || !owner.IsActive || owner.MerchantId is null || owner.WalletType != "Deposit")
                continue; // not a merchant deposit address — ignore (platform inflows are handled elsewhere)

            var deposit = DepositEntity.Record(
                transfer.Chain, transfer.Address, owner.WalletId, owner.MerchantId.Value, transfer.AssetId,
                transfer.Amount, transfer.TransactionHash, transfer.OutputIndex, transfer.BlockNumber, transfer.BlockHash,
                policy, timeProvider.GetUtcNow());

            if (deposit.IsFailure)
            {
                logger.LogWarning(
                    "Skipping malformed transfer {TxHash}:{OutputIndex} on {Chain}: {Error}",
                    transfer.TransactionHash, transfer.OutputIndex, chain, deposit.Error!.Code);
                continue;
            }

            var outcome = await repository.AddIfNewAsync(deposit.Value, cancellationToken);
            if (outcome == DepositRecordOutcome.Recorded)
                recorded++;
        }

        // Advance the cursor only after the whole window is processed, so a crash re-scans, never skips.
        await cursors.SetLastScannedBlockAsync(chain, toBlock, cancellationToken);
        return recorded;
    }
}
