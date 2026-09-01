using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>
/// The merchant-side half of the two-party payout rule: one of a merchant's portal users submits a payout, and
/// one of their approvers signs it off before the platform ever evaluates it.
///
/// <para><b>Tenant-scoped by construction.</b> Every method takes the caller's <c>merchantId</c> and refuses a
/// withdrawal belonging to anyone else — reported as "not found", so the response never confirms that another
/// merchant's withdrawal id is real.</para>
///
/// <para><b>A merchant cannot approve past the platform gate.</b> Approval only decides that the merchant is
/// happy with the payout; where it goes next is re-resolved here against the effective approval threshold
/// (per-merchant override, else platform config, §10) — at or below it the payout is cleared to send, above it
/// it still waits for platform staff. Rejection releases the ledger reserve, exactly as a platform rejection does.</para>
/// </summary>
public interface IMerchantPayoutApprovalService
{
    Task<Result<WithdrawalResult>> ApproveAsync(
        Guid merchantId, Guid withdrawalId, string approvedBy, CancellationToken cancellationToken = default);

    Task<Result<WithdrawalResult>> RejectAsync(
        Guid merchantId, Guid withdrawalId, string rejectedBy, string reason, CancellationToken cancellationToken = default);
}

public sealed class MerchantPayoutApprovalService(
    IWithdrawalRepository repository,
    IWithdrawalPolicyProvider policies,
    IMerchantApprovalThreshold merchantApprovalThreshold,
    TimeProvider timeProvider) : IMerchantPayoutApprovalService
{
    public async Task<Result<WithdrawalResult>> ApproveAsync(
        Guid merchantId, Guid withdrawalId, string approvedBy, CancellationToken cancellationToken = default)
    {
        var withdrawal = await GetOwnedAsync(merchantId, withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure<WithdrawalResult>(WithdrawalErrors.NotFound);

        // Re-resolved at approval time, consistent with the request-time and processing-time gates: the merchant
        // decides only whether to proceed, never whether the platform needs to look at it.
        var merchantThreshold = await merchantApprovalThreshold.GetAsync(
            withdrawal.MerchantId, withdrawal.AssetId, cancellationToken);
        var threshold = merchantThreshold ?? policies.For(withdrawal.Chain).ApprovalThreshold;
        var requiresPlatformApproval = withdrawal.Amount > threshold;

        var result = withdrawal.MerchantApprove(approvedBy, requiresPlatformApproval, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return Result.Failure<WithdrawalResult>(result.Error!);

        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(new WithdrawalResult(withdrawal.Id, withdrawal.Status.ToString()));
    }

    public async Task<Result<WithdrawalResult>> RejectAsync(
        Guid merchantId, Guid withdrawalId, string rejectedBy, string reason, CancellationToken cancellationToken = default)
    {
        var withdrawal = await GetOwnedAsync(merchantId, withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure<WithdrawalResult>(WithdrawalErrors.NotFound);

        var result = withdrawal.MerchantReject(rejectedBy, reason, timeProvider.GetUtcNow());
        if (result.IsFailure)
            return Result.Failure<WithdrawalResult>(result.Error!);

        // The reject raised a release event on the aggregate; saving publishes it via the outbox so the Ledger
        // returns the reserved funds (the same path a platform rejection uses).
        await repository.SaveChangesAsync(cancellationToken);
        return Result.Success(new WithdrawalResult(withdrawal.Id, withdrawal.Status.ToString()));
    }

    /// <summary>Loads the withdrawal only if it belongs to this tenant — the isolation check, not an afterthought.</summary>
    private async Task<Domain.Withdrawal?> GetOwnedAsync(Guid merchantId, Guid withdrawalId, CancellationToken cancellationToken)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        return withdrawal is null || withdrawal.MerchantId != merchantId ? null : withdrawal;
    }
}
