using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

public sealed record RequestWithdrawalCommand(
    Guid MerchantId, Guid AssetId, Chain Chain, string DestinationAddress, BigInteger Amount, string MerchantTransactionId,
    string? CallbackUrl = null);

public sealed record WithdrawalResult(Guid WithdrawalId, string Status);

public interface IWithdrawalRequestService
{
    Task<Result<WithdrawalResult>> RequestAsync(RequestWithdrawalCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Accepts a withdrawal request: validates policy + merchant standing, then <b>creates the record and
/// reserves the funds</b> (synchronous, via the Ledger's balance-guarded reserve). A resent merchant
/// transaction id is <b>rejected</b> (<see cref="WithdrawalErrors.DuplicateReference"/>) — we never pay the
/// same reference twice; the unique <c>(MerchantId, MerchantTransactionId)</c> index is the real double-pay
/// guard. A record still in <see cref="WithdrawalStatus.Reserving"/> (a crash mid-reserve) is the one case
/// we resume instead of reject, so a partially-created withdrawal is never stranded.
/// </summary>
public sealed class WithdrawalRequestService(
    IWithdrawalRepository repository,
    IWithdrawalPolicyProvider policies,
    IMerchantDirectory merchants,
    IMerchantFeeSchedule feeSchedule,
    IWithdrawalLedger ledger,
    TimeProvider timeProvider) : IWithdrawalRequestService
{
    public async Task<Result<WithdrawalResult>> RequestAsync(RequestWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var policy = policies.For(command.Chain);
        var withdrawal = await repository.FindByMerchantTransactionIdAsync(command.MerchantId, command.MerchantTransactionId, cancellationToken);

        // Reject a resent merchant transaction id — we never pay the same reference twice (the merchant may
        // resubmit after a timeout). The sole exception is a record still in Reserving: that is a crash/partial
        // before the funds hold finished, so we resume the SAME record below (never a second payout) rather
        // than strand the merchant's withdrawal.
        if (withdrawal is not null && withdrawal.Status != WithdrawalStatus.Reserving)
            return Result.Failure<WithdrawalResult>(WithdrawalErrors.DuplicateReference);

        if (withdrawal is null)
        {
            // First time: validate, then create the record in Reserving. The unique
            // (MerchantId, MerchantTransactionId) index is the real double-withdrawal guard.
            var merchant = await merchants.FindByIdAsync(command.MerchantId, cancellationToken);
            if (merchant is null || !merchant.CanTransact)
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.MerchantCannotTransact);

            if (policy.IsBelowMinimum(command.Amount))
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.BelowMinimum);
            if (policy.ExceedsMaximum(command.Amount))
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.AboveMaximum);

            // Pricing is per-merchant (fixed + %), resolved from the Merchant module — the source of truth,
            // superseding the config policy's flat fee. The merchant bears this fee; the platform bears gas.
            var fee = await feeSchedule.QuoteWithdrawalFeeAsync(command.MerchantId, command.AssetId, command.Amount, cancellationToken);

            var created = WithdrawalEntity.Request(
                command.MerchantId, command.AssetId, command.Chain, command.DestinationAddress,
                command.Amount, fee, command.MerchantTransactionId, command.CallbackUrl, timeProvider.GetUtcNow());
            if (created.IsFailure)
                return Result.Failure<WithdrawalResult>(created.Error!);

            withdrawal = created.Value;
            if (await repository.AddIfNewAsync(withdrawal, cancellationToken) == WithdrawalRecordOutcome.Duplicate)
            {
                // Lost a concurrent create race — adopt the winner. If it already advanced past Reserving it is
                // a genuine duplicate; reject rather than return it, matching the resend rule above.
                var winner = await repository.FindByMerchantTransactionIdAsync(command.MerchantId, command.MerchantTransactionId, cancellationToken)
                    ?? throw new DomainException("Duplicate withdrawal with no surviving record — impossible state.");
                if (winner.Status != WithdrawalStatus.Reserving)
                    return Result.Failure<WithdrawalResult>(WithdrawalErrors.DuplicateReference);
                withdrawal = winner;
            }
        }

        // Reserve (idempotent) if not yet done — covers a fresh request and a crash-before-reserve resume.
        if (withdrawal.Status == WithdrawalStatus.Reserving)
        {
            var reserve = await ledger.ReserveAsync(
                new ReserveWithdrawalRequest(withdrawal.Id, withdrawal.MerchantId, withdrawal.AssetId, withdrawal.Amount, withdrawal.Fee),
                cancellationToken);

            if (reserve.IsFailure)
            {
                withdrawal.MarkReserveFailed(reserve.Error!.Message, timeProvider.GetUtcNow());
                await repository.SaveChangesAsync(cancellationToken);
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.InsufficientBalance);
            }

            withdrawal.ConfirmReserved(policy.RequiresApproval(withdrawal.Amount), timeProvider.GetUtcNow());
            await repository.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new WithdrawalResult(withdrawal.Id, withdrawal.Status.ToString()));
    }
}
