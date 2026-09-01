using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>What the operator gets back after acting on a settlement.</summary>
public sealed record SettlementActionResult(Guid WithdrawalId, string Status);

public interface IMerchantSettlementService
{
    /// <summary>An admin has audited the request and cleared it for finance to pay.</summary>
    Task<Result<SettlementActionResult>> AuditApproveAsync(
        Guid withdrawalId, string auditedBy, CancellationToken cancellationToken = default);

    /// <summary>An admin declines the settlement — the merchant's reserved funds are returned to available.</summary>
    Task<Result<SettlementActionResult>> AuditRejectAsync(
        Guid withdrawalId, string auditedBy, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finance paid the merchant from a company wallet and is recording it. The hash is verified on-chain
    /// BEFORE anything is written; a failed check leaves the settlement exactly where it was.
    /// </summary>
    Task<Result<SettlementActionResult>> RecordSettlementAsync(
        Guid withdrawalId, string completedBy, string transactionHash, string? sourceAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The merchant-settlement (cash-out) workflow. This system does <b>not</b> pay a merchant settlement: an
/// operations admin audits the request, a finance admin pays the merchant from a company wallet OUTSIDE
/// platform custody, and the resulting transaction is recorded here.
///
/// <para>The merchant's funds are reserved from the moment they request the cash-out and stay reserved
/// throughout, so the balance can neither be spent twice nor stranded — a rejection at either stage returns
/// it. The reserve is discharged only when a real, confirmed, on-chain payment has been verified.</para>
///
/// <para><b>Verification is the whole point of this service.</b> Everything else here is state transitions the
/// aggregate already guards; what only an Application service can do is check the chain before letting a
/// human's claim discharge a merchant's money (§14).</para>
/// </summary>
public sealed class MerchantSettlementService(
    IWithdrawalRepository repository,
    ITransactionVerifier verifier,
    ILogger<MerchantSettlementService> logger) : IMerchantSettlementService
{
    public async Task<Result<SettlementActionResult>> AuditApproveAsync(
        Guid withdrawalId, string auditedBy, CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure<SettlementActionResult>(WithdrawalErrors.NotFound);

        var result = withdrawal.AdminAuditApprove(auditedBy, DateTimeOffset.UtcNow);
        if (result.IsFailure)
            return Result.Failure<SettlementActionResult>(result.Error!);

        await repository.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Settlement {WithdrawalId} audited by {AuditedBy}; awaiting finance transfer.", withdrawalId, auditedBy);

        return Result.Success(new SettlementActionResult(withdrawalId, withdrawal.Status.ToString()));
    }

    public async Task<Result<SettlementActionResult>> AuditRejectAsync(
        Guid withdrawalId, string auditedBy, string reason, CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure<SettlementActionResult>(WithdrawalErrors.NotFound);

        // Raises WithdrawalFailed, which the Ledger consumes to release the reserve. Saving the aggregate and
        // its outbox message in one transaction is what makes the release durable rather than best-effort.
        var result = withdrawal.AdminAuditReject(auditedBy, reason, DateTimeOffset.UtcNow);
        if (result.IsFailure)
            return Result.Failure<SettlementActionResult>(result.Error!);

        await repository.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Settlement {WithdrawalId} rejected by {AuditedBy}: {Reason}.", withdrawalId, auditedBy, reason);

        return Result.Success(new SettlementActionResult(withdrawalId, withdrawal.Status.ToString()));
    }

    public async Task<Result<SettlementActionResult>> RecordSettlementAsync(
        Guid withdrawalId, string completedBy, string transactionHash, string? sourceAddress,
        CancellationToken cancellationToken = default)
    {
        var withdrawal = await repository.GetByIdAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
            return Result.Failure<SettlementActionResult>(WithdrawalErrors.NotFound);

        // Refuse early if the settlement is not actually awaiting payment, so we never spend a chain lookup —
        // or worse, report a verification failure — for a withdrawal that could not be settled anyway.
        if (withdrawal.Status != WithdrawalStatus.PendingFinanceTransfer)
            return Result.Failure<SettlementActionResult>(WithdrawalErrors.InvalidStateTransition);

        // VERIFY BEFORE RECORDING. The operator is asserting that a payment happened; until the chain agrees,
        // nothing is written and the settlement stays exactly where it was, free to be corrected and resubmitted.
        // The merchant is owed AT LEAST the net amount — the fee is the platform's, not theirs.
        var expected = withdrawal.Amount;
        var verification = await verifier.VerifyAsync(
            new VerifyTransferRequest(
                withdrawal.Chain, transactionHash, withdrawal.DestinationAddress, withdrawal.AssetId, expected),
            cancellationToken);

        if (verification.IsFailure)
        {
            logger.LogWarning(
                "Settlement {WithdrawalId}: hash {Hash} refused ({Code}). Nothing recorded.",
                withdrawalId, transactionHash, verification.Error!.Code);
            return Result.Failure<SettlementActionResult>(verification.Error!);
        }

        var result = withdrawal.RecordFinanceSettlement(
            completedBy, verification.Value.TransactionHash, sourceAddress ?? string.Empty, DateTimeOffset.UtcNow);
        if (result.IsFailure)
            return Result.Failure<SettlementActionResult>(result.Error!);

        // A duplicate hash is caught by UX_Withdrawal_SettlementTxHash, not by a prior read — two operators
        // recording the same transfer concurrently would both pass a check-then-act, and one merchant would be
        // paid for the other's money.
        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsDuplicateSettlementHash(ex))
        {
            logger.LogWarning(
                "Settlement {WithdrawalId}: hash {Hash} is already recorded against another settlement.",
                withdrawalId, transactionHash);
            return Result.Failure<SettlementActionResult>(WithdrawalErrors.DuplicateSettlementHash);
        }

        logger.LogInformation(
            "Settlement {WithdrawalId} recorded by {CompletedBy} against verified tx {Hash} in block {Block}.",
            withdrawalId, completedBy, verification.Value.TransactionHash, verification.Value.BlockNumber);

        return Result.Success(new SettlementActionResult(withdrawalId, withdrawal.Status.ToString()));
    }

    private static bool IsDuplicateSettlementHash(Exception ex) =>
        ex.ToString().Contains("UX_Withdrawal_SettlementTxHash", StringComparison.OrdinalIgnoreCase);
}
