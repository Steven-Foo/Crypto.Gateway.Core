using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using Microsoft.Extensions.Logging;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// Drives approved withdrawals through build → sign → broadcast. The private key never enters this
/// service — it hands an unsigned blob and a key reference to <see cref="ISigner"/> and receives a
/// signed blob (§10). Crash-safe: build/sign are deterministic and the chain dedups on the tx hash, so
/// re-processing a withdrawal stuck in <see cref="WithdrawalStatus.Signing"/> cannot double-send.
/// (The real transaction builder must pin the nonce per withdrawal to preserve that — a deferred note.)
/// A pre-broadcast failure releases the reserved funds; a broadcast failure likewise (nothing left the chain).
///
/// <para><b>Pool allocation + physical gate.</b> Before signing, it allocates a hot wallet from the platform
/// pool via <see cref="IHotWalletAllocator"/> — one that is not already leased (one tx at a time per wallet,
/// held until confirmed) and holds enough on-chain, chosen least-recently-used. If none is available (all
/// leased or underfunded) the withdrawal is <em>parked</em> (<see cref="WithdrawalStatus.AwaitingFunds"/>) with
/// the reserve HELD — the merchant is still owed it — and auto-resumes on a later pass. A payout above the
/// approval threshold that is otherwise ready is held for a human to release
/// (<see cref="WithdrawalStatus.AwaitingRelease"/>): the "large = manual" rule. Ledger sufficiency and physical
/// availability are independent; this gate is the physical one.</para>
/// </summary>
public sealed class WithdrawalProcessingService(
    IWithdrawalRepository repository,
    ITransactionBuilder transactionBuilder,
    ISigner signer,
    ITransactionBroadcaster broadcaster,
    IHotWalletAllocator allocator,
    IWithdrawalPolicyProvider policies,
    IEnergyDelegationService energy,
    TimeProvider timeProvider,
    ILogger<WithdrawalProcessingService> logger)
{
    // Both funding holds are re-scanned so a parked withdrawal auto-re-evaluates and resumes when the float recovers.
    private static readonly WithdrawalStatus[] Processable =
        [WithdrawalStatus.Approved, WithdrawalStatus.Signing, WithdrawalStatus.AwaitingFunds, WithdrawalStatus.AwaitingRelease];

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var pending = await repository.GetByStatusesAsync(Processable, cancellationToken);
        var processed = 0;

        foreach (var withdrawal in pending)
        {
            if (await ProcessOneAsync(withdrawal, cancellationToken))
                processed++;
        }

        return processed;
    }

    private async Task<bool> ProcessOneAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken)
    {
        try
        {
            // Already signed (a resumed/retried pass): re-broadcast the SAME persisted blob — never rebuild.
            // Rebuilding would mint a new tx id the chain won't dedup, risking a double-send.
            if (withdrawal.Status == WithdrawalStatus.Signing && withdrawal.HasSignedTransaction)
                return await BroadcastAsync(withdrawal, withdrawal.SignedTransaction!, cancellationToken);

            // Allocate a hot wallet from the pool: not currently leased (one tx at a time, held until confirmed),
            // holding at least the amount on-chain, least-recently-used. None available ⇒ park (reserve held).
            var lease = await allocator.AllocateAsync(withdrawal.Chain, withdrawal.AssetId, withdrawal.Amount, cancellationToken);
            if (lease is null)
                return await HoldForFundsAsync(withdrawal, cancellationToken);

            // A payout above the approval threshold needs a human release before it sends (the "large = manual"
            // rule); a release already granted (ReleasedAt set) clears it for good, so a later re-park never
            // demands a second release. Small ones send automatically.
            var threshold = policies.For(withdrawal.Chain).ApprovalThreshold;
            if (withdrawal.Amount > threshold && withdrawal.ReleasedAt is null)
                return await HoldForReleaseAsync(withdrawal, threshold, cancellationToken);

            // Cleared to send. Return a parked withdrawal to Approved and persist that BEFORE the send attempt,
            // so a build/sign fault can't leave an unsaved in-memory transition to be flushed by another entity.
            if (withdrawal.Status is WithdrawalStatus.AwaitingFunds or WithdrawalStatus.AwaitingRelease)
            {
                if (withdrawal.ResumeToApproved(timeProvider.GetUtcNow()).IsFailure)
                    return false; // state moved under us — leave for the next pass
                await repository.SaveChangesAsync(cancellationToken);
            }

            // Energy gate: a USDT payout is a TRC-20 transfer FROM the hot pool wallet, so — like a sweep — it needs
            // energy (+ bandwidth) or it burns ~27 TRX. Provision on-demand from the gas hub (delegate energy, top up
            // bandwidth). Not Ready yet ⇒ leave the withdrawal Approved and retry next pass (transient — the
            // delegation/top-up confirms in a few blocks); never sign without energy. In dev the in-memory resource
            // reader reports healthy energy ⇒ Ready immediately, so the happy path is unaffected.
            var readiness = await energy.EnsureEnergyForTransferAsync(withdrawal.Chain, lease.Address, cancellationToken);
            if (readiness != EnergyReadiness.Ready)
            {
                logger.LogInformation(
                    "Withdrawal {WithdrawalId} waiting on energy for hot wallet {Address} ({Readiness}).",
                    withdrawal.Id, lease.Address, readiness);
                return false; // retried next pass once energy is provisioned
            }

            // Approved: build → sign FROM the leased wallet → persist the signed blob (→ Signing, stamping the
            // SourceWalletId that leases the wallet) → broadcast.
            var unsigned = await transactionBuilder.BuildTransferAsync(
                new BuildWithdrawalRequest(withdrawal.Chain, withdrawal.AssetId, lease.Address, withdrawal.DestinationAddress, withdrawal.Amount),
                cancellationToken);

            var signed = await signer.SignAsync(
                new SigningRequest(withdrawal.Id, withdrawal.Chain, unsigned.Payload, lease.KeyReference), cancellationToken);
            if (signed.IsFailure)
                return await FailAsync(withdrawal, $"sign: {signed.Error!.Message}", cancellationToken); // pre-broadcast → safe to release

            // Persist the signed blob + SourceWalletId atomically with the → Signing transition. A crash after
            // this re-broadcasts this exact blob (above); a crash before it leaves us Approved to rebuild
            // safely. The filtered unique index on SourceWalletId is the DB backstop should two workers ever
            // race for the same wallet — the loser's SaveChanges throws and it retries with another wallet next
            // pass (handled by the outer catch). Today a single worker + per-withdrawal busy re-query in the
            // allocator already makes that race impossible.
            var recorded = withdrawal.RecordSigned(Guid.CreateVersion7(), lease.WalletId, signed.Value.SignedPayload, timeProvider.GetUtcNow());
            if (recorded.IsFailure)
                return false; // state moved under us — leave for the next pass

            // Persist under the concurrency guards: the unique index rejects a wallet another withdrawal just
            // leased, rowversion rejects a withdrawal another worker just advanced. Either ⇒ re-allocate next
            // pass; nothing was broadcast, so no double-send (§7.4 — the DB is the correctness guard).
            if (!await repository.TrySaveSignedAsync(withdrawal, cancellationToken))
            {
                logger.LogInformation(
                    "Withdrawal {WithdrawalId} lost hot wallet {WalletId} to a concurrent lease; will re-allocate next pass.",
                    withdrawal.Id, lease.WalletId);
                return false;
            }

            return await BroadcastAsync(withdrawal, signed.Value.SignedPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            // Ambiguous failures (e.g. a lost broadcast ack) land here: no state change, retried next pass.
            // A withdrawal already in Signing is re-broadcast (idempotent), never released, so this cannot double-spend.
            logger.LogError(ex, "Processing withdrawal {WithdrawalId} failed; will retry next pass.", withdrawal.Id);
            return false;
        }
    }

    private async Task<bool> BroadcastAsync(WithdrawalEntity withdrawal, byte[] signedPayload, CancellationToken cancellationToken)
    {
        var broadcast = await broadcaster.BroadcastAsync(withdrawal.Chain, signedPayload, cancellationToken);
        if (broadcast.IsFailure)
        {
            // A definitive node rejection (result:false) means the transaction was NOT accepted — nothing
            // reached the chain — so releasing the reserve is safe. A duplicate is mapped to success by the
            // broadcaster, so a re-broadcast of an accepted tx never lands here. (A lost ack throws instead,
            // handled by the caller's catch: no release.)
            return await FailAsync(withdrawal, $"broadcast: {broadcast.Error!.Message}", cancellationToken);
        }

        var marked = withdrawal.MarkBroadcast(broadcast.Value.TransactionHash, timeProvider.GetUtcNow());
        if (marked.IsFailure)
            return false;
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> FailAsync(WithdrawalEntity withdrawal, string reason, CancellationToken cancellationToken)
    {
        if (withdrawal.Fail(reason, timeProvider.GetUtcNow()).IsSuccess)
        {
            await repository.SaveChangesAsync(cancellationToken); // raises WithdrawalFailed → Ledger release
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parks a withdrawal for which no pool wallet is available (all leased, or none holds the amount) — reserve
    /// HELD, ops-traceable reason recorded. Only writes (and logs) on the transition INTO the hold, not on every
    /// pass it stays parked, so a chronically busy/empty pool doesn't spam the log or the DB. Returns false.
    /// </summary>
    private async Task<bool> HoldForFundsAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken)
    {
        if (withdrawal.Status == WithdrawalStatus.AwaitingFunds)
            return false; // already parked — no write, no log

        var reason = $"No hot wallet available: every pool wallet is busy or holds less than {withdrawal.Amount} (base units).";
        if (withdrawal.Park(reason, timeProvider.GetUtcNow()).IsFailure)
            return false;

        await repository.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Withdrawal {WithdrawalId} on {Chain} awaiting funds — {Reason} Reload a hot wallet from treasury to resume.",
            withdrawal.Id, withdrawal.Chain, reason);
        return false;
    }

    /// <summary>
    /// Holds an above-threshold payout that is funded but needs a human release (the "large = manual" rule).
    /// Transition-only write/log, same as <see cref="HoldForFundsAsync"/>. Returns false: nothing was sent.
    /// </summary>
    private async Task<bool> HoldForReleaseAsync(WithdrawalEntity withdrawal, BigInteger threshold, CancellationToken cancellationToken)
    {
        if (withdrawal.Status == WithdrawalStatus.AwaitingRelease)
            return false;

        var reason = $"Amount {withdrawal.Amount} exceeds the auto-send threshold {threshold} (base units) — awaiting operator release.";
        if (withdrawal.MarkAwaitingRelease(reason, timeProvider.GetUtcNow()).IsFailure)
            return false;

        await repository.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Withdrawal {WithdrawalId} on {Chain} awaiting operator release — {Reason}", withdrawal.Id, withdrawal.Chain, reason);
        return false;
    }
}
