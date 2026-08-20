using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>A merchant cashing out its own earnings. No destination — it always goes to the merchant's
/// pre-registered settlement wallet for the chain (§10).</summary>
public sealed record MerchantWithdrawalCommand(
    Guid MerchantId, Guid AssetId, Chain Chain, BigInteger Amount, string MerchantTransactionId, string? CallbackUrl = null);

public interface IMerchantWithdrawalService
{
    Task<Result<WithdrawalResult>> RequestAsync(MerchantWithdrawalCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Accepts a <b>merchant withdrawal</b> — the merchant cashing out its own earnings (<see cref="WithdrawalKind.Merchant"/>).
/// Distinct from the user-payout <see cref="WithdrawalRequestService"/>: the destination is resolved from the
/// whitelisted settlement wallet (never client-supplied), and the amount is gated by the flat/% liquidity cap
/// instead of the per-transaction min/max. It charges the SAME withdrawal fee as a user payout and shares the
/// identical ledger reserve + downstream pipeline (the ledger never learns the kind). A resent
/// <c>(MerchantId, txnId)</c> is rejected; a crash-stranded <see cref="WithdrawalStatus.Reserving"/> record is
/// resumed, never double-paid.
/// </summary>
public sealed class MerchantWithdrawalService(
    IWithdrawalRepository repository,
    IWithdrawalPolicyProvider policies,
    IMerchantDirectory merchants,
    IMerchantSettlementDirectory settlements,
    IMerchantWithdrawalCap caps,
    IMerchantFeeSchedule feeSchedule,
    IMerchantApprovalThreshold merchantApprovalThreshold,
    SettledBalanceGate settledBalance,
    IWithdrawalLedger ledger,
    TimeProvider timeProvider) : IMerchantWithdrawalService
{
    public async Task<Result<WithdrawalResult>> RequestAsync(MerchantWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var policy = policies.For(command.Chain);
        var withdrawal = await repository.FindByMerchantTransactionIdAsync(
            command.MerchantId, WithdrawalKind.Merchant, command.MerchantTransactionId, cancellationToken);

        // Reject a resent reference — never cash out twice. A record still Reserving is a crash mid-reserve:
        // resume the SAME record below rather than strand it (mirrors the user-withdrawal rule).
        if (withdrawal is not null && withdrawal.Status != WithdrawalStatus.Reserving)
            return Result.Failure<WithdrawalResult>(WithdrawalErrors.DuplicateReference);

        if (withdrawal is null)
        {
            var merchant = await merchants.FindByIdAsync(command.MerchantId, cancellationToken);
            if (merchant is null || !merchant.CanTransact)
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.MerchantCannotTransact);

            // Destination is the whitelisted settlement wallet — NEVER client-supplied (§10).
            var destination = await settlements.FindSettlementAddressAsync(command.MerchantId, command.Chain, cancellationToken);
            if (string.IsNullOrWhiteSpace(destination))
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.SettlementWalletNotRegistered);

            // Settled (withdrawable) balance — only funds matured past the merchant's T+N may cash out; also the
            // base for the percentage liquidity cap below. At T+0 this equals the full balance, so the settled
            // reject is skipped (the reserve remains the balance guard) and the cap is taken against the full
            // balance, as before. Best-effort pre-check; the reserve stays the atomic overdraw guard (settled ≤ total).
            var settled = await settledBalance.GetSettledAvailableAsync(
                command.MerchantId, command.AssetId, merchant.SettlementDelayDays, cancellationToken);
            if (merchant.SettlementDelayDays > 0 && command.Amount > settled)
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.ExceedsSettledBalance);

            // Liquidity cap: min(flat, ⌊settled·bps/10000⌋) across whichever caps are set.
            var cap = await caps.GetAsync(command.MerchantId, command.AssetId, cancellationToken);
            if (cap.HasCap && command.Amount > EffectiveCap(cap, settled))
                return Result.Failure<WithdrawalResult>(WithdrawalErrors.ExceedsMerchantWithdrawalLimit);

            // The same per-merchant withdrawal fee as a user payout — the merchant bears it; the platform bears gas.
            var fee = await feeSchedule.QuoteWithdrawalFeeAsync(command.MerchantId, command.AssetId, command.Amount, cancellationToken);

            var created = WithdrawalEntity.Request(
                command.MerchantId, command.AssetId, command.Chain, destination, command.Amount, fee,
                command.MerchantTransactionId, command.CallbackUrl, timeProvider.GetUtcNow(), WithdrawalKind.Merchant);
            if (created.IsFailure)
                return Result.Failure<WithdrawalResult>(created.Error!);

            withdrawal = created.Value;
            if (await repository.AddIfNewAsync(withdrawal, cancellationToken) == WithdrawalRecordOutcome.Duplicate)
            {
                var winner = await repository.FindByMerchantTransactionIdAsync(
                    command.MerchantId, WithdrawalKind.Merchant, command.MerchantTransactionId, cancellationToken)
                    ?? throw new DomainException("Duplicate merchant withdrawal with no surviving record — impossible state.");
                if (winner.Status != WithdrawalStatus.Reserving)
                    return Result.Failure<WithdrawalResult>(WithdrawalErrors.DuplicateReference);
                withdrawal = winner;
            }
        }

        // Reserve (idempotent) if not yet done — the atomic sufficiency check, identical to a user withdrawal.
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

            // Per-merchant approval-threshold override of the config default (§10), same as a user payout: a
            // cash-out above the effective threshold enters PendingApproval. Unset ⇒ the platform config threshold.
            var merchantThreshold = await merchantApprovalThreshold.GetAsync(withdrawal.MerchantId, withdrawal.AssetId, cancellationToken);
            var requiresApproval = withdrawal.Amount > (merchantThreshold ?? policy.ApprovalThreshold);
            withdrawal.ConfirmReserved(requiresApproval, timeProvider.GetUtcNow());
            await repository.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new WithdrawalResult(withdrawal.Id, withdrawal.Status.ToString()));
    }

    /// <summary>The most restrictive of the configured caps, in base units. The percent cap is taken against
    /// the merchant's <paramref name="settledAvailable"/> balance (only settled funds are cashable). Floored
    /// integer math (§14).</summary>
    private static BigInteger EffectiveCap(MerchantWithdrawalCap cap, BigInteger settledAvailable)
    {
        BigInteger? effective = cap.FlatCap;

        if (cap.PercentBps > 0)
        {
            var percentCap = settledAvailable * cap.PercentBps / 10_000;
            effective = effective is { } flat ? BigInteger.Min(flat, percentCap) : percentCap;
        }

        return effective ?? settledAvailable; // HasCap guarantees a branch set it; fall through is a no-op cap
    }
}
