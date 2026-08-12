using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.KeyManagement.Contracts;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Application;

/// <summary>
/// Drives Pending sweeps through build → sign → broadcast, mirroring the withdrawal money-out path. The
/// private key never enters this service — it hands an unsigned blob plus the deposit address's key
/// <em>reference</em> (resolved via <see cref="IDepositSigningKeyDirectory"/>) to <see cref="ISigner"/> and
/// receives a signed blob (§10). Crash-safe: the signed blob is persisted before broadcast, so re-processing
/// a sweep stuck in <see cref="SweepStatus.Signing"/> re-broadcasts the SAME transaction (the chain dedups on
/// tx id) rather than building a new one. A pre-broadcast failure just marks the sweep Failed — there is
/// nothing to release, because a sweep never reserved anything (the funds were always ours).
/// </summary>
public sealed class SweepProcessingService(
    ISweepRepository repository,
    ITransactionBuilder transactionBuilder,
    ISigner signer,
    ITransactionBroadcaster broadcaster,
    IDepositSigningKeyDirectory signingKeys,
    IEnergyDelegationService energy,
    TimeProvider timeProvider,
    ILogger<SweepProcessingService> logger)
{
    private static readonly SweepStatus[] Processable = [SweepStatus.Pending, SweepStatus.Signing];

    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var pending = await repository.GetByStatusesAsync(Processable, cancellationToken);
        var processed = 0;

        foreach (var sweep in pending)
        {
            if (await ProcessOneAsync(sweep, cancellationToken))
                processed++;
        }

        return processed;
    }

    private async Task<bool> ProcessOneAsync(Domain.Sweep sweep, CancellationToken cancellationToken)
    {
        try
        {
            // Already signed (a resumed/retried pass): re-broadcast the SAME persisted blob — never rebuild.
            if (sweep.Status == SweepStatus.Signing && sweep.HasSignedTransaction)
                return await BroadcastAsync(sweep, sweep.SignedTransaction!, cancellationToken);

            // Gate on energy (§4.5, via Energy.Contracts): a TRC-20 sweep from a deposit address needs energy or
            // it burns TRX the address doesn't have. Ask Energy to ready the source address; if it isn't Ready
            // yet, leave the sweep Pending and retry next pass — never build/broadcast without energy, but never
            // fail the sweep either (the funds are safe). In dev the in-memory resource reader reports healthy
            // energy, so this returns Ready immediately and the sweep proceeds with no delegation.
            var readiness = await energy.EnsureEnergyForTransferAsync(sweep.Chain, sweep.FromAddress, cancellationToken);
            if (readiness != EnergyReadiness.Ready)
            {
                logger.LogInformation("Sweep {SweepId} waiting on energy for {Address} ({Readiness}).", sweep.Id, sweep.FromAddress, readiness);
                return false; // leave Pending; retried next pass once energy is delegated
            }

            // Resolve the deposit address's signing key. Null ⇒ no active key for this address ⇒ fail (nothing
            // was signed or broadcast, and a sweep has nothing to release).
            var signingKey = await signingKeys.FindByAddressAsync(sweep.Chain, sweep.FromAddress, cancellationToken);
            if (signingKey is null)
                return await FailAsync(sweep, $"no signing key for deposit address {sweep.FromAddress}", cancellationToken);

            var unsigned = await transactionBuilder.BuildTransferAsync(
                new BuildWithdrawalRequest(sweep.Chain, sweep.AssetId, sweep.FromAddress, sweep.ToAddress, sweep.Amount),
                cancellationToken);

            var signed = await signer.SignAsync(
                new SigningRequest(sweep.Id, sweep.Chain, unsigned.Payload, signingKey.KeyReference), cancellationToken);
            if (signed.IsFailure)
                return await FailAsync(sweep, $"sign: {signed.Error!.Message}", cancellationToken);

            var recorded = sweep.RecordSigned(Guid.CreateVersion7(), signed.Value.SignedPayload, timeProvider.GetUtcNow());
            if (recorded.IsFailure)
                return false; // state moved under us — leave for the next pass
            await repository.SaveChangesAsync(cancellationToken);

            return await BroadcastAsync(sweep, signed.Value.SignedPayload, cancellationToken);
        }
        catch (Exception ex)
        {
            // Ambiguous failures (e.g. a lost broadcast ack) land here: no state change, retried next pass. A
            // sweep already in Signing is re-broadcast (idempotent), never failed, so this cannot double-send.
            logger.LogError(ex, "Processing sweep {SweepId} failed; will retry next pass.", sweep.Id);
            return false;
        }
    }

    private async Task<bool> BroadcastAsync(Domain.Sweep sweep, byte[] signedPayload, CancellationToken cancellationToken)
    {
        var broadcast = await broadcaster.BroadcastAsync(sweep.Chain, signedPayload, cancellationToken);
        if (broadcast.IsFailure)
        {
            // A definitive node rejection means nothing reached the chain — safe to mark Failed. A duplicate is
            // mapped to success by the broadcaster, so a re-broadcast of an accepted tx never lands here.
            return await FailAsync(sweep, $"broadcast: {broadcast.Error!.Message}", cancellationToken);
        }

        var marked = sweep.MarkBroadcast(broadcast.Value.TransactionHash, timeProvider.GetUtcNow());
        if (marked.IsFailure)
            return false;
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> FailAsync(Domain.Sweep sweep, string reason, CancellationToken cancellationToken)
    {
        if (sweep.Fail(reason, timeProvider.GetUtcNow()).IsSuccess)
        {
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }
}
