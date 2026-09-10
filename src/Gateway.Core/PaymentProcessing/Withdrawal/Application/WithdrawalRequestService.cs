using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <remarks>
/// There is deliberately no "requires merchant approval" flag here. Whether a payout waits for the merchant's
/// own approver is that merchant's stored policy (<c>MerchantSummary.RequiresPayoutApproval</c>), resolved
/// inside the service, so the two entry points cannot disagree. A caller-supplied flag is exactly how that
/// decision came to mean "which host received the request" instead of "what the merchant wants".
/// </remarks>
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
    IMerchantWithdrawalLimits merchantLimits,
    IMerchantApprovalThreshold merchantApprovalThreshold,
    SettledBalanceGate settledBalance,
    IWithdrawalLedger ledger,
    IAddressEncoderFactory addressEncoders,
    TimeProvider timeProvider) : IWithdrawalRequestService
{
    public async Task<Result<WithdrawalResult>> RequestAsync(RequestWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        // Format-only check, before anything else is touched: catches a typo or malformed address outright,
        // so no funds are ever reserved against a destination that could never receive them. It proves
        // nothing about whether the address belongs to anyone, or is the one the caller actually meant —
        // no software can tell that apart from a different, equally valid address (§ IAddressEncoder).
        if (addressEncoders.Supports(command.Chain) && !addressEncoders.For(command.Chain).IsValidAddress(command.DestinationAddress))
            return Result.Failure<WithdrawalResult>(WithdrawalErrors.DestinationInvalid);

        var policy = policies.For(command.Chain);
        var withdrawal = await repository.FindByMerchantTransactionIdAsync(command.MerchantId, WithdrawalKind.User, command.MerchantTransactionId, cancellationToken);

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

            // Per-merchant min/max override the platform config default when set; an unset (null) bound falls
            // back to config. The merchant value fully overrides — staff can raise or lower a merchant's limits.
            var limits = await merchantLimits.GetAsync(command.MerchantId, command.AssetId, cancellationToken);
            var effectiveMinimum = limits.Minimum ?? policy.Minimum;
            var effectiveMaximum = limits.Maximum ?? policy.Maximum;

            if (command.Amount < effectiveMinimum)
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.BelowMinimum);
            if (effectiveMaximum is { } max && command.Amount > max)
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.AboveMaximum);

            // Settlement period (T+N): only funds matured past it may leave. Skipped entirely at T+0, where the
            // ledger reserve is the sole balance guard (behaviour unchanged for merchants with no settlement
            // period). Best-effort pre-check — the reserve below stays the atomic overdraw guard on the total
            // balance (settled ≤ total, so a within-settled amount never fails the reserve for lack of funds).
            if (merchant.SettlementDelayDays > 0)
            {
                var settled = await settledBalance.GetSettledAvailableAsync(
                    command.MerchantId, command.AssetId, merchant.SettlementDelayDays, cancellationToken);
                if (command.Amount > settled)
                    return Result.Failure<WithdrawalResult>(WithdrawalErrors.ExceedsSettledBalance);
            }

            // Pricing is per-merchant (fixed + %, floored at the minimum-fee), resolved from the Merchant
            // module — the source of truth, superseding the config policy's flat fee. The merchant bears this
            // fee; the platform bears gas. The rate inputs travel with it onto the record (fee transparency).
            var quote = await feeSchedule.QuoteWithdrawalFeeAsync(command.MerchantId, command.AssetId, command.Amount, cancellationToken);

            var created = WithdrawalEntity.Request(
                command.MerchantId, command.AssetId, command.Chain, command.DestinationAddress,
                command.Amount, quote.Fee, command.MerchantTransactionId, command.CallbackUrl, timeProvider.GetUtcNow(),
                feeBps: quote.Bps, feeFixed: quote.Fixed, feeMinimum: quote.Minimum, minimumFeeApplied: quote.MinimumApplied);
            if (created.IsFailure)
                return Result.Failure<WithdrawalResult>(created.Error!);

            withdrawal = created.Value;
            if (await repository.AddIfNewAsync(withdrawal, cancellationToken) == WithdrawalRecordOutcome.Duplicate)
            {
                // Lost a concurrent create race — adopt the winner. If it already advanced past Reserving it is
                // a genuine duplicate; reject rather than return it, matching the resend rule above.
                var winner = await repository.FindByMerchantTransactionIdAsync(command.MerchantId, WithdrawalKind.User, command.MerchantTransactionId, cancellationToken)
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

            // Per-merchant approval-threshold override of the config default (§10): a payout above the effective
            // threshold enters PendingApproval, else Approved. Unset ⇒ the platform config threshold.
            var merchantThreshold = await merchantApprovalThreshold.GetAsync(withdrawal.MerchantId, withdrawal.AssetId, cancellationToken);
            var requiresApproval = withdrawal.Amount > (merchantThreshold ?? policy.ApprovalThreshold);

            // Whether this payout first waits for the MERCHANT's own approver is the merchant's policy, read
            // here rather than taken from the caller. Deciding it in the service (not in a host) is what makes
            // the two entry points agree: previously the portal hardcoded "yes" and the HMAC API "no", so the
            // flag recorded which host was called instead of what the merchant wanted — and a merchant
            // integrating server-to-server could never reach the approval queue at all.
            //
            // Resolved at the decision point rather than in the create block above, so the crash-resume path
            // (which re-enters here without having re-read the merchant) applies the identical policy.
            // Absent merchant ⇒ false: never strand a payout awaiting an approval nobody was asked for.
            var merchantPolicy = await merchants.FindByIdAsync(withdrawal.MerchantId, cancellationToken);
            var requiresMerchantApproval = merchantPolicy?.RequiresPayoutApproval ?? false;

            withdrawal.ConfirmReserved(requiresApproval, timeProvider.GetUtcNow(), requiresMerchantApproval);
            await repository.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new WithdrawalResult(withdrawal.Id, withdrawal.Status.ToString()));
    }
}
