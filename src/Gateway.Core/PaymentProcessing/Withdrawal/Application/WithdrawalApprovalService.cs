using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

public interface IWithdrawalApprovalService
{
    Task<Result> ApproveAsync(Guid withdrawalId, string approver, CancellationToken cancellationToken = default);

    Task<Result> RejectAsync(Guid withdrawalId, string approver, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Human approval for withdrawals above the threshold (§10). Rejection releases the reserved funds via the outbox.</summary>
public sealed class WithdrawalApprovalService(IWithdrawalRepository repository, TimeProvider timeProvider) : IWithdrawalApprovalService
{
    public async Task<Result> ApproveAsync(Guid withdrawalId, string approver, CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure(WithdrawalErrors.NotFound);

        var result = withdrawal.Approve(approver, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(Guid withdrawalId, string approver, string reason, CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure(WithdrawalErrors.NotFound);

        var result = withdrawal.Reject(approver, reason, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken); // raises WithdrawalFailed → Ledger release
        return Result.Success();
    }
}
