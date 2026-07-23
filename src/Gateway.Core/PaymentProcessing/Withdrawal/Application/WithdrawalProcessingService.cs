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
/// </summary>
public sealed class WithdrawalProcessingService(
    IWithdrawalRepository repository,
    ITransactionBuilder transactionBuilder,
    ISigner signer,
    ITransactionBroadcaster broadcaster,
    IHotWalletProvider hotWallets,
    TimeProvider timeProvider,
    ILogger<WithdrawalProcessingService> logger)
{
    private static readonly WithdrawalStatus[] Processable = [WithdrawalStatus.Approved, WithdrawalStatus.Signing];

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

            // Fresh (Approved): build → sign → persist the signed blob (→ Signing) → broadcast.
            var hotWallet = hotWallets.For(withdrawal.Chain);

            var unsigned = await transactionBuilder.BuildTransferAsync(
                new BuildWithdrawalRequest(withdrawal.Chain, withdrawal.AssetId, hotWallet.Address, withdrawal.DestinationAddress, withdrawal.Amount),
                cancellationToken);

            var signed = await signer.SignAsync(
                new SigningRequest(withdrawal.Id, withdrawal.Chain, unsigned.Payload, hotWallet.KeyReference), cancellationToken);
            if (signed.IsFailure)
                return await FailAsync(withdrawal, $"sign: {signed.Error!.Message}", cancellationToken); // pre-broadcast → safe to release

            // Persist the signed blob atomically with the → Signing transition. If we crash after this, the
            // next pass re-broadcasts this exact blob (above); if we crash before it, we are still Approved and
            // rebuild safely (nothing signed or broadcast yet).
            var recorded = withdrawal.RecordSigned(Guid.CreateVersion7(), signed.Value.SignedPayload, timeProvider.GetUtcNow());
            if (recorded.IsFailure)
                return false; // state moved under us — leave for the next pass
            await repository.SaveChangesAsync(cancellationToken);

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
}
