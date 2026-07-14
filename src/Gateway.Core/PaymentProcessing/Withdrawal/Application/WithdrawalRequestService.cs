using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

public sealed record RequestWithdrawalCommand(
    Guid MerchantId, Guid AssetId, Chain Chain, string DestinationAddress, BigInteger Amount, string IdempotencyKey);

public sealed record WithdrawalResult(Guid WithdrawalId, string Status);

public interface IWithdrawalRequestService
{
    Task<Result<WithdrawalResult>> RequestAsync(RequestWithdrawalCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Accepts a withdrawal request: validates policy + merchant standing, then <b>creates the record and
/// reserves the funds</b> (synchronous, via the Ledger's balance-guarded reserve). Idempotent on the
/// client key — a retry returns the same withdrawal, and a crash mid-reserve resumes cleanly because the
/// record is created in <see cref="WithdrawalStatus.Reserving"/> first and the reserve is idempotent.
/// </summary>
public sealed class WithdrawalRequestService(
    IWithdrawalRepository repository,
    IWithdrawalPolicyProvider policies,
    IMerchantDirectory merchants,
    IWithdrawalLedger ledger,
    TimeProvider timeProvider) : IWithdrawalRequestService
{
    public async Task<Result<WithdrawalResult>> RequestAsync(RequestWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var policy = policies.For(command.Chain);
        var withdrawal = await repository.FindByIdempotencyKeyAsync(command.MerchantId, command.IdempotencyKey, cancellationToken);

        if (withdrawal is null)
        {
            // First time: validate, then create the record (deduped by the idempotency key).
            var merchant = await merchants.FindByIdAsync(command.MerchantId, cancellationToken);
            if (merchant is null || !merchant.CanTransact)
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.MerchantCannotTransact);

            if (policy.IsBelowMinimum(command.Amount))
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.BelowMinimum);
            if (policy.ExceedsMaximum(command.Amount))
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.AboveMaximum);

            var created = WithdrawalEntity.Request(
                command.MerchantId, command.AssetId, command.Chain, command.DestinationAddress,
                command.Amount, policy.Fee, command.IdempotencyKey, timeProvider.GetUtcNow());
            if (created.IsFailure)
                return Result.Failure<WithdrawalResult>(created.Error!);

            withdrawal = created.Value;
            if (await repository.AddIfNewAsync(withdrawal, cancellationToken) == WithdrawalRecordOutcome.Duplicate)
            {
                // Lost a concurrent create race — adopt the winner.
                withdrawal = await repository.FindByIdempotencyKeyAsync(command.MerchantId, command.IdempotencyKey, cancellationToken)
                    ?? throw new DomainException("Idempotency violation with no surviving withdrawal — impossible state.");
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
