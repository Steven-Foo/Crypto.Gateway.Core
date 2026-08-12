using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;

/// <summary>
/// Confirms broadcast energy operations once buried under the policy depth. There is no ledger settle — a
/// stake/delegate changed no custody — so confirmation just finalises the operation's own state (the energy
/// is now staked or delegated). A transaction that reverted on-chain is left in Broadcast and flagged for ops.
/// </summary>
public sealed class EnergyOperationConfirmationService(
    IEnergyOperationRepository repository,
    ITransactionBroadcaster broadcaster,
    IChainStatusReader chainStatus,
    EnergyOperationOptions options,
    TimeProvider timeProvider,
    ILogger<EnergyOperationConfirmationService> logger)
{
    private static readonly EnergyOperationStatus[] Broadcast = [EnergyOperationStatus.Broadcast];

    public async Task<int> TrackOnceAsync(CancellationToken cancellationToken = default)
    {
        var broadcast = await repository.GetByStatusesAsync(Broadcast, cancellationToken);
        if (broadcast.Count == 0)
            return 0;

        var now = timeProvider.GetUtcNow();
        var confirmedCount = 0;

        foreach (var operation in broadcast)
        {
            var status = await broadcaster.GetTransactionStatusAsync(operation.Chain, operation.TransactionHash!, cancellationToken);
            if (status is null)
                continue; // not mined yet

            if (!status.Succeeded)
            {
                logger.LogError(
                    "Energy operation {OperationId} ({Kind}) transaction {TxHash} reverted on-chain — left for ops.",
                    operation.Id, operation.Kind, operation.TransactionHash);
                continue;
            }

            var tip = await chainStatus.GetTipHeightAsync(operation.Chain, cancellationToken);
            var confirmations = (int)Math.Max(0, tip - status.BlockNumber + 1);

            operation.RecordConfirmations(confirmations, now);

            if (confirmations >= options.Confirmations && operation.Confirm(now).IsSuccess)
                confirmedCount++;
        }

        await repository.SaveChangesAsync(cancellationToken);
        return confirmedCount;
    }
}
