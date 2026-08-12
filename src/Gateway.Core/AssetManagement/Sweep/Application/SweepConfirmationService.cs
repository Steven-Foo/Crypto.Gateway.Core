using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;

/// <summary>
/// Watches broadcast sweeps and confirms them once buried under the policy's confirmation depth. Unlike a
/// withdrawal there is no ledger settle — custody didn't change — so confirmation just finalises the sweep's
/// own state (the funds are now concentrated in the hot wallet). A transaction that reverted on-chain is left
/// in Broadcast and flagged for ops, never confirmed.
/// </summary>
public sealed class SweepConfirmationService(
    ISweepRepository repository,
    ITransactionBroadcaster broadcaster,
    IChainStatusReader chainStatus,
    ISweepPolicyProvider policies,
    TimeProvider timeProvider,
    ILogger<SweepConfirmationService> logger)
{
    private static readonly SweepStatus[] Broadcast = [SweepStatus.Broadcast];

    public async Task<int> TrackOnceAsync(CancellationToken cancellationToken = default)
    {
        var broadcast = await repository.GetByStatusesAsync(Broadcast, cancellationToken);
        if (broadcast.Count == 0)
            return 0;

        var now = timeProvider.GetUtcNow();
        var confirmedCount = 0;

        foreach (var sweep in broadcast)
        {
            var status = await broadcaster.GetTransactionStatusAsync(sweep.Chain, sweep.TransactionHash!, cancellationToken);
            if (status is null)
                continue; // not mined yet

            if (!status.Succeeded)
            {
                logger.LogError(
                    "Sweep {SweepId} transaction {TxHash} reverted on-chain — left for ops, not confirmed.",
                    sweep.Id, sweep.TransactionHash);
                continue;
            }

            var tip = await chainStatus.GetTipHeightAsync(sweep.Chain, cancellationToken);
            var confirmations = (int)Math.Max(0, tip - status.BlockNumber + 1);

            sweep.RecordConfirmations(confirmations, now);

            if (confirmations >= policies.For(sweep.Chain).Confirmations && sweep.Confirm(now).IsSuccess)
                confirmedCount++;
        }

        await repository.SaveChangesAsync(cancellationToken);
        return confirmedCount;
    }
}
