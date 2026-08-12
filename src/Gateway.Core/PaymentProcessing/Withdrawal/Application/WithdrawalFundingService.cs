using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

public interface IWithdrawalFundingService
{
    /// <summary>Releases an above-threshold parked withdrawal for sending (the "large = manual" resume). The
    /// processing worker sends it on the next pass once the float is sufficient.</summary>
    Task<Result> ReleaseAsync(Guid withdrawalId, string operatorId, CancellationToken cancellationToken = default);

    /// <summary>Abandons a parked withdrawal that cannot be funded — the one path that releases the reserve
    /// from a hold, so the merchant's funds return.</summary>
    Task<Result> CancelAsync(Guid withdrawalId, string operatorId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ops actions on withdrawals parked by the physical float gate (<see cref="WithdrawalStatus.AwaitingFunds"/>/
/// <see cref="WithdrawalStatus.AwaitingRelease"/>). Distinct from approval (<see cref="IWithdrawalApprovalService"/>),
/// which gates a withdrawal before it is ever processed; this acts after processing has found the hot wallet
/// short or the amount above the auto-send threshold. Registered in the read/ops composer so a host that only
/// exposes ops screens (no signer/broadcaster) can drive it (§4.7).
/// </summary>
public sealed class WithdrawalFundingService(IWithdrawalRepository repository, TimeProvider timeProvider) : IWithdrawalFundingService
{
    public async Task<Result> ReleaseAsync(Guid withdrawalId, string operatorId, CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure(WithdrawalErrors.NotFound);

        var result = withdrawal.ReleaseForSend(operatorId, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(Guid withdrawalId, string operatorId, string reason, CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure(WithdrawalErrors.NotFound);

        var result = withdrawal.Cancel(operatorId, reason, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return result;

        await repository.SaveChangesAsync(cancellationToken); // raises WithdrawalFailed → Ledger release
        return Result.Success();
    }
}
