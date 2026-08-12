using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;

/// <summary>How deep a reload must bury before it counts as confirmed. Supplied by the host (dev = 1).</summary>
public sealed class TreasuryReloadOptions
{
    public int Confirmations { get; init; } = 1;
}

/// <summary>
/// Drives submitted reloads: broadcasts the operator's signed blob (<c>Signed</c> → <c>Broadcast</c>) and
/// confirms it once buried (<c>Broadcast</c> → <c>Confirmed</c>). Crash-safe: the signed blob is persisted, so a
/// retry re-broadcasts the same transaction (the chain dedups on its id). On confirmation the target hot
/// wallet's float has increased on-chain — the withdrawal allocator picks that up next pass and any parked
/// <c>AwaitingFunds</c> withdrawals resume, with no ledger entry involved (§14).
/// </summary>
public sealed class TreasuryReloadProcessingService(
    ITreasuryReloadRepository repository,
    ITransactionBroadcaster broadcaster,
    IChainStatusReader chainStatus,
    TreasuryReloadOptions options,
    TimeProvider timeProvider,
    ILogger<TreasuryReloadProcessingService> logger)
{
    private static readonly TreasuryReloadStatus[] Active = [TreasuryReloadStatus.Signed, TreasuryReloadStatus.Broadcast];

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var reloads = await repository.GetByStatusesAsync(Active, cancellationToken);
        var changed = 0;

        foreach (var reload in reloads)
            if (await ProcessOneAsync(reload, cancellationToken))
                changed++;

        return changed;
    }

    private async Task<bool> ProcessOneAsync(TreasuryReload reload, CancellationToken cancellationToken)
    {
        try
        {
            return reload.Status switch
            {
                TreasuryReloadStatus.Signed => await BroadcastAsync(reload, cancellationToken),
                TreasuryReloadStatus.Broadcast => await ConfirmAsync(reload, cancellationToken),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            // Ambiguous failures (e.g. a lost broadcast ack) land here: no state change, retried next pass. A
            // reload already Broadcast is re-confirmed idempotently, never re-broadcast, so this is safe.
            logger.LogError(ex, "Processing treasury reload {ReloadId} failed; will retry next pass.", reload.Id);
            return false;
        }
    }

    private async Task<bool> BroadcastAsync(TreasuryReload reload, CancellationToken cancellationToken)
    {
        var broadcast = await broadcaster.BroadcastAsync(reload.Chain, reload.SignedTransaction!, cancellationToken);
        if (broadcast.IsFailure)
        {
            // A definitive node rejection means nothing reached the chain — fail it (the operator re-initiates).
            // A lost ack throws instead and is retried by the caller's catch, never failed.
            if (reload.Fail($"broadcast: {broadcast.Error!.Message}", timeProvider.GetUtcNow()).IsSuccess)
                await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        var marked = reload.MarkBroadcast(broadcast.Value.TransactionHash, timeProvider.GetUtcNow());
        if (marked.IsFailure)
            return false;

        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ConfirmAsync(TreasuryReload reload, CancellationToken cancellationToken)
    {
        var status = await broadcaster.GetTransactionStatusAsync(reload.Chain, reload.TransactionHash!, cancellationToken);
        if (status is null)
            return false; // not mined yet

        if (!status.Succeeded)
        {
            logger.LogError(
                "Treasury reload {ReloadId} transaction {TxHash} reverted on-chain — left for ops.",
                reload.Id, reload.TransactionHash);
            return false;
        }

        var tip = await chainStatus.GetTipHeightAsync(reload.Chain, cancellationToken);
        var confirmations = (int)Math.Max(0, tip - status.BlockNumber + 1);
        reload.RecordConfirmations(confirmations, timeProvider.GetUtcNow());

        var confirmed = confirmations >= options.Confirmations && reload.Confirm(timeProvider.GetUtcNow()).IsSuccess;
        await repository.SaveChangesAsync(cancellationToken); // persist confirmation-depth progress regardless
        return confirmed;
    }
}
