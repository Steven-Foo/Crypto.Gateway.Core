using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>An operator recording company funds they have already moved into a hot withdrawal wallet.</summary>
public sealed record RecordTopUpRequest(
    Chain Chain, Guid AssetId, Guid TargetWalletId, BigInteger Amount, string TransactionHash,
    string? SourceAddress, string RecordedBy);

public sealed record RecordedTopUp(Guid TopUpId, string TargetAddress, BigInteger Amount);

public interface IHotWalletTopUpService
{
    Task<Result<RecordedTopUp>> RecordAsync(RecordTopUpRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Records company funds moved into a hot withdrawal wallet, so the automated payout pipeline stays funded.
///
/// <para>The transfer has already happened: an operator sent it from a company wallet outside platform
/// custody and is now telling us about it. So the order here is <b>verify, then record, then post</b> — the
/// chain is consulted before anything is written, because this posting <em>raises recorded custody</em>, and
/// an unverified claim would inflate <c>TreasuryAsset</c> against funds that never arrived. Reconciliation
/// would then report drift forever with no way to tell which top-up was fictional.</para>
///
/// <para>The ledger posting is <c>Dr TreasuryAsset / Cr WithdrawalWalletTopUp</c>: it never touches a merchant
/// account, so operating float can never be mistaken for merchant earnings (§14).</para>
/// </summary>
public sealed class HotWalletTopUpService(
    IHotWalletTopUpRepository repository,
    ITreasuryHotWalletDirectory hotWallets,
    ITransactionVerifier verifier,
    TimeProvider timeProvider,
    ILogger<HotWalletTopUpService> logger) : IHotWalletTopUpService
{
    public async Task<Result<RecordedTopUp>> RecordAsync(
        RecordTopUpRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Amount <= BigInteger.Zero)
            return Result.Failure<RecordedTopUp>(WithdrawalErrors.AmountNotPositive);

        if (string.IsNullOrWhiteSpace(request.TransactionHash))
            return Result.Failure<RecordedTopUp>(WithdrawalErrors.TransactionHashRequired);

        // The destination must be one of OUR hot-pool wallets. An operator picks which wallet to top up, but
        // not whether the destination is ours: crediting custody for a transfer into someone else's address
        // would be inventing custody outright.
        var pool = await hotWallets.GetHotWalletPoolAsync(request.Chain, cancellationToken);
        var target = pool.FirstOrDefault(w => w.WalletId == request.TargetWalletId);
        if (target is null)
            return Result.Failure<RecordedTopUp>(WithdrawalErrors.HotWalletNotFound);

        // Cheap pre-check so a re-submitted hash reports the real reason rather than surfacing as a save
        // conflict. The unique index below is still the arbiter — this read only improves the message.
        if (await repository.FindByTransactionHashAsync(request.TransactionHash, cancellationToken) is not null)
            return Result.Failure<RecordedTopUp>(WithdrawalErrors.DuplicateSettlementHash);

        // VERIFY BEFORE RECORDING: confirmed on chain, to this wallet, this asset, at least this amount.
        var verification = await verifier.VerifyAsync(
            new VerifyTransferRequest(
                request.Chain, request.TransactionHash, target.Address, request.AssetId, request.Amount),
            cancellationToken);

        if (verification.IsFailure)
        {
            logger.LogWarning(
                "Top-up of {Wallet}: hash {Hash} refused ({Code}). Nothing recorded, nothing posted.",
                target.Address, request.TransactionHash, verification.Error!.Code);
            return Result.Failure<RecordedTopUp>(verification.Error!);
        }

        // Record the amount the CHAIN shows, not the amount typed. If an operator sent more than they entered,
        // custody must reflect what actually arrived — otherwise reconciliation drifts by the difference.
        var actual = verification.Value.Amount;

        var topUp = HotWalletTopUp.Record(
            request.Chain, request.AssetId, target.WalletId, target.Address, actual,
            verification.Value.TransactionHash, request.SourceAddress, request.RecordedBy,
            timeProvider.GetUtcNow());

        if (topUp.IsFailure)
            return Result.Failure<RecordedTopUp>(topUp.Error!);

        // The record and its outbox message commit together, so the ledger posting is durable: it cannot be
        // lost by a crash here, and it cannot be applied twice (the ledger is idempotent on the top-up id).
        var outcome = await repository.AddIfNewAsync(topUp.Value, cancellationToken);
        if (outcome == TopUpRecordOutcome.Duplicate)
            return Result.Failure<RecordedTopUp>(WithdrawalErrors.DuplicateSettlementHash);

        logger.LogInformation(
            "Recorded top-up {TopUpId}: {Amount} into {Wallet} by {RecordedBy}, verified tx {Hash}.",
            topUp.Value.Id, actual, target.Address, request.RecordedBy, verification.Value.TransactionHash);

        return Result.Success(new RecordedTopUp(topUp.Value.Id, target.Address, actual));
    }
}
