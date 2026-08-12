using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// Watches broadcast withdrawals and confirms them once buried under the policy's confirmation depth,
/// raising <c>WithdrawalConfirmed</c> → the Ledger settles. A transaction that reverted on-chain is left
/// in Broadcast and flagged for ops — never settled, since funds may not have moved as intended.
/// </summary>
public sealed class WithdrawalConfirmationService(
    IWithdrawalRepository repository,
    ITransactionBroadcaster broadcaster,
    IChainStatusReader chainStatus,
    IWithdrawalPolicyProvider policies,
    GasAccountingOptions gasAccounting,
    TimeProvider timeProvider,
    ILogger<WithdrawalConfirmationService> logger)
{
    private static readonly WithdrawalStatus[] Broadcast = [WithdrawalStatus.Broadcast];

    public async Task<int> TrackOnceAsync(CancellationToken cancellationToken = default)
    {
        var broadcast = await repository.GetByStatusesAsync(Broadcast, cancellationToken);
        if (broadcast.Count == 0)
            return 0;

        var now = timeProvider.GetUtcNow();
        var confirmedCount = 0;

        foreach (var withdrawal in broadcast)
        {
            var status = await broadcaster.GetTransactionStatusAsync(withdrawal.Chain, withdrawal.TransactionHash!, cancellationToken);
            if (status is null)
                continue; // not mined yet

            if (!status.Succeeded)
            {
                logger.LogError(
                    "Withdrawal {WithdrawalId} transaction {TxHash} reverted on-chain — left for ops, not settled.",
                    withdrawal.Id, withdrawal.TransactionHash);
                continue;
            }

            var tip = await chainStatus.GetTipHeightAsync(withdrawal.Chain, cancellationToken);
            var confirmations = (int)Math.Max(0, tip - status.BlockNumber + 1);

            // Recorded every pass (ops visibility), independent of whether it already clears the policy depth.
            withdrawal.RecordConfirmations(confirmations, now);

            // Carry the actual on-chain fee + its gas asset onto the confirmation event so the Ledger books the
            // platform gas cost (5c). Both are inert in dev (in-memory engine charges no fee) and where no gas
            // asset is configured for the chain.
            if (confirmations >= policies.For(withdrawal.Chain).Confirmations
                && withdrawal.Confirm(now, status.FeeSun, gasAccounting.Resolve(withdrawal.Chain)).IsSuccess)
                confirmedCount++;
        }

        // Always save: confirmation-depth progress is worth persisting even before a withdrawal fully confirms.
        await repository.SaveChangesAsync(cancellationToken); // Confirm() also raises WithdrawalConfirmed → Ledger settle

        return confirmedCount;
    }
}
