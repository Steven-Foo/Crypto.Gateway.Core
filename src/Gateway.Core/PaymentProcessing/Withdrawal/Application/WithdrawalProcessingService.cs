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
            var hotWallet = hotWallets.For(withdrawal.Chain);

            var unsigned = await transactionBuilder.BuildTransferAsync(
                new BuildWithdrawalRequest(withdrawal.Chain, withdrawal.AssetId, hotWallet.Address, withdrawal.DestinationAddress, withdrawal.Amount),
                cancellationToken);

            if (withdrawal.Status == WithdrawalStatus.Approved)
            {
                withdrawal.BeginSigning(Guid.CreateVersion7(), timeProvider.GetUtcNow());
                await repository.SaveChangesAsync(cancellationToken);
            }

            var signed = await signer.SignAsync(
                new SigningRequest(withdrawal.Id, withdrawal.Chain, unsigned.Payload, hotWallet.KeyReference), cancellationToken);
            if (signed.IsFailure)
                return await FailAsync(withdrawal, $"sign: {signed.Error!.Message}", cancellationToken);

            var broadcast = await broadcaster.BroadcastAsync(withdrawal.Chain, signed.Value.SignedPayload, cancellationToken);
            if (broadcast.IsFailure)
                return await FailAsync(withdrawal, $"broadcast: {broadcast.Error!.Message}", cancellationToken);

            withdrawal.MarkBroadcast(broadcast.Value.TransactionHash, timeProvider.GetUtcNow());
            await repository.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing withdrawal {WithdrawalId} failed; will retry next pass.", withdrawal.Id);
            return false;
        }
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
