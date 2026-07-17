using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application;

/// <summary>
/// Advances tracked deposits toward confirmation and detects reorgs. For each pending deposit it either:
/// <list type="bullet">
///   <item>finds the block still canonical → updates confirmations, and at the policy threshold promotes
///   it to Confirmed (raising <c>DepositConfirmed</c> → the Ledger credits);</item>
///   <item>finds the block gone or its hash changed → marks it Orphaned (raising <c>DepositOrphaned</c>
///   only if it had been confirmed, so the Ledger reverses exactly what it credited).</item>
/// </list>
/// All mutations for a pass commit in one transaction, so deposit state and the outbox events that
/// depend on it are always consistent. Runs single-writer per chain (a background worker).
/// </summary>
public sealed class DepositConfirmationService(
    IChainStatusReader chainStatus,
    IDepositRepository repository,
    IDepositPolicyProvider policies,
    TimeProvider timeProvider,
    ILogger<DepositConfirmationService> logger)
{
    /// <summary>Processes one confirmation pass for a chain. Returns how many deposits changed state.</summary>
    public async Task<int> TrackOnceAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        var tracked = await repository.GetTrackableAsync(chain, cancellationToken);
        if (tracked.Count == 0)
            return 0;

        var tip = await chainStatus.GetTipHeightAsync(chain, cancellationToken);
        var finalizedHeight = await chainStatus.GetFinalizedHeightAsync(chain, cancellationToken);
        var policy = policies.For(chain);
        var now = timeProvider.GetUtcNow();

        var changed = 0;
        foreach (var deposit in tracked)
        {
            var statusBefore = deposit.Status;
            var canonical = await chainStatus.GetBlockAsync(chain, deposit.BlockNumber, cancellationToken);

            if (canonical is null || !string.Equals(canonical.BlockHash, deposit.BlockHash, StringComparison.Ordinal))
            {
                // The block that carried this deposit is no longer on the canonical chain.
                var wasConfirmed = deposit.IsConfirmed;
                deposit.MarkOrphaned(now);
                if (wasConfirmed)
                    logger.LogWarning(
                        "Deposit {DepositId} orphaned AFTER confirmation on {Chain} — a reorg deeper than the confirmation depth; ledger reversal raised for ops review.",
                        deposit.Id, chain);
            }
            else
            {
                var confirmations = checked((int)Math.Max(0, tip - deposit.BlockNumber + 1));
                var isFinalized = deposit.BlockNumber <= finalizedHeight;
                deposit.RegisterConfirmations(confirmations, isFinalized, policy, now);

                // Once the carrying block is irreversible there is nothing left to watch, so retire the
                // deposit from the trackable set. Without this the tracker re-checks every deposit ever
                // taken on every pass — one RPC each, forever — which grows without bound and exhausts the
                // node's rate limit. A no-op unless the deposit is credited (see MarkFinalized).
                if (isFinalized)
                    deposit.MarkFinalized(now);
            }

            if (deposit.Status != statusBefore)
                changed++;
        }

        await repository.SaveChangesAsync(cancellationToken);
        return changed;
    }
}
