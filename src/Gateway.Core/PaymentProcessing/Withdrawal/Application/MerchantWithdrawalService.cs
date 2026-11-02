using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;

/// <summary>A merchant cashing out its own earnings. Never a free-form destination — it goes to one of the
/// merchant's staff-approved settlement wallets for the chain (§10). <paramref name="SettlementWalletId"/> picks an
/// approved one (primary or standby); null means the active (primary) wallet.</summary>
public sealed record MerchantWithdrawalCommand(
    Guid MerchantId, Guid AssetId, Chain Chain, BigInteger Amount, string MerchantTransactionId, string? CallbackUrl = null,
    Guid? SettlementWalletId = null);

/// <summary>A preview of a cash-out the merchant is about to submit — the command's inputs minus the reference.</summary>
public sealed record MerchantWithdrawalQuoteRequest(
    Guid MerchantId, Guid AssetId, Chain Chain, BigInteger Amount, Guid? SettlementWalletId = null);

/// <summary>
/// What a cash-out of <see cref="Amount"/> would do, worked out by the SAME checks and fee calculation the real
/// cash-out runs, so the preview cannot disagree with what is charged. All amounts are base units (§14).
///
/// <para>The merchant's wallet receives the full <see cref="Amount"/>; the fee comes off the balance ON TOP, so the
/// balance drops by <see cref="TotalDebit"/>. <see cref="Refusal"/> is the error the real cash-out would return right
/// now, or null if it would be accepted — best effort, since the balance can move before the merchant submits.</para>
/// </summary>
public sealed record MerchantWithdrawalQuote(
    BigInteger Amount,
    BigInteger Fee,
    BigInteger TotalDebit,
    BigInteger AvailableBalance,
    BigInteger SettledBalance,
    BigInteger? CashOutLimit,
    string? DestinationAddress,
    bool? DestinationIsPrimary,
    Error? Refusal);

/// <summary>An accepted cash-out, with what was actually charged and where it is going (base units).</summary>
public sealed record MerchantWithdrawalResult(
    Guid WithdrawalId, string Status, string DestinationAddress, BigInteger Amount, BigInteger Fee, BigInteger TotalDebit);

public interface IMerchantWithdrawalService
{
    Task<Result<WithdrawalResult>> RequestAsync(MerchantWithdrawalCommand command, CancellationToken cancellationToken = default);

    /// <summary>The same request as <see cref="RequestAsync"/>, also returning what was charged and the destination.</summary>
    Task<Result<MerchantWithdrawalResult>> RequestWithDetailsAsync(MerchantWithdrawalCommand command, CancellationToken cancellationToken = default);

    /// <summary>Previews a cash-out without creating or reserving anything.</summary>
    Task<Result<MerchantWithdrawalQuote>> QuoteAsync(MerchantWithdrawalQuoteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Accepts a <b>merchant withdrawal</b> — the merchant cashing out its own earnings (<see cref="WithdrawalKind.Merchant"/>).
/// Distinct from the user-payout <see cref="WithdrawalRequestService"/>: the destination is one of the merchant's
/// staff-approved settlement wallets (never a client-supplied address), and the amount is gated by the flat/%
/// liquidity cap instead of the per-transaction min/max. It charges the SAME withdrawal fee as a user payout and
/// shares the identical ledger reserve + downstream pipeline (the ledger never learns the kind). A resent
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
    IAddressEncoderFactory addressEncoders,
    TimeProvider timeProvider) : IMerchantWithdrawalService
{
    public async Task<Result<WithdrawalResult>> RequestAsync(MerchantWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var result = await RequestWithDetailsAsync(command, cancellationToken);
        return result.IsFailure
            ? Result.Failure<WithdrawalResult>(result.Error!)
            : Result.Success(new WithdrawalResult(result.Value.WithdrawalId, result.Value.Status));
    }

    public async Task<Result<MerchantWithdrawalQuote>> QuoteAsync(
        MerchantWithdrawalQuoteRequest request, CancellationToken cancellationToken = default)
    {
        var check = await EvaluateAsync(
            request.MerchantId, request.AssetId, request.Chain, request.Amount, request.SettlementWalletId, cancellationToken);

        var totalDebit = request.Amount + check.Fee.Fee;

        // The real request lets the ledger reserve refuse an over-balance cash-out; the preview reserves nothing,
        // so it asks the same question of the current balance. On submit the reserve stays the atomic guard.
        var refusal = check.Error;
        if (refusal is null && totalDebit > check.Available)
            refusal = WithdrawalErrors.InsufficientBalance;

        return Result.Success(new MerchantWithdrawalQuote(
            request.Amount, check.Fee.Fee, totalDebit, check.Available, check.Settled, check.CashOutLimit,
            check.Wallet?.Address, check.Wallet?.IsActive, refusal));
    }

    public async Task<Result<MerchantWithdrawalResult>> RequestWithDetailsAsync(
        MerchantWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var policy = policies.For(command.Chain);
        var withdrawal = await repository.FindByMerchantTransactionIdAsync(
            command.MerchantId, WithdrawalKind.Merchant, command.MerchantTransactionId, cancellationToken);

        // Reject a resent reference — never cash out twice. A record still Reserving is a crash mid-reserve:
        // resume the SAME record below rather than strand it (mirrors the user-withdrawal rule).
        if (withdrawal is not null && withdrawal.Status != WithdrawalStatus.Reserving)
            return Result.Failure<MerchantWithdrawalResult>(WithdrawalErrors.DuplicateReference);

        if (withdrawal is null)
        {
            var check = await EvaluateAsync(
                command.MerchantId, command.AssetId, command.Chain, command.Amount, command.SettlementWalletId, cancellationToken);
            if (check.Error is not null)
                return Result.Failure<MerchantWithdrawalResult>(check.Error);

            var quote = check.Fee;
            var created = WithdrawalEntity.Request(
                command.MerchantId, command.AssetId, command.Chain, check.Wallet!.Address, command.Amount, quote.Fee,
                command.MerchantTransactionId, command.CallbackUrl, timeProvider.GetUtcNow(), WithdrawalKind.Merchant,
                feeBps: quote.Bps, feeFixed: quote.Fixed, feeMinimum: quote.Minimum, minimumFeeApplied: quote.MinimumApplied);
            if (created.IsFailure)
                return Result.Failure<MerchantWithdrawalResult>(created.Error!);

            withdrawal = created.Value;
            if (await repository.AddIfNewAsync(withdrawal, cancellationToken) == WithdrawalRecordOutcome.Duplicate)
            {
                var winner = await repository.FindByMerchantTransactionIdAsync(
                    command.MerchantId, WithdrawalKind.Merchant, command.MerchantTransactionId, cancellationToken)
                    ?? throw new DomainException("Duplicate merchant withdrawal with no surviving record — impossible state.");
                if (winner.Status != WithdrawalStatus.Reserving)
                    return Result.Failure<MerchantWithdrawalResult>(WithdrawalErrors.DuplicateReference);
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
                return Result.Failure<MerchantWithdrawalResult>(WithdrawalErrors.InsufficientBalance);
            }

            // Per-merchant approval-threshold override of the config default (§10), same as a user payout: a
            // cash-out above the effective threshold enters PendingApproval. Unset ⇒ the platform config threshold.
            var merchantThreshold = await merchantApprovalThreshold.GetAsync(withdrawal.MerchantId, withdrawal.AssetId, cancellationToken);
            var requiresApproval = withdrawal.Amount > (merchantThreshold ?? policy.ApprovalThreshold);
            withdrawal.ConfirmReserved(requiresApproval, timeProvider.GetUtcNow());
            await repository.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(new MerchantWithdrawalResult(
            withdrawal.Id, withdrawal.Status.ToString(), withdrawal.DestinationAddress,
            withdrawal.Amount, withdrawal.Fee, withdrawal.Amount + withdrawal.Fee));
    }

    /// <summary>What <see cref="EvaluateAsync"/> found: the first refusal (if any) plus every figure worked out on
    /// the way, so the preview can still show the balances and the fee alongside a refusal.</summary>
    private sealed record Evaluation(
        Error? Error,
        ApprovedSettlementWallet? Wallet,
        FeeQuote Fee,
        BigInteger Available,
        BigInteger Settled,
        BigInteger? CashOutLimit);

    /// <summary>
    /// Every pre-reserve check a cash-out makes, in order, shared by the real request and the preview so the two
    /// can never drift apart. Keeps the FIRST refusal; the remaining figures are still filled in.
    /// </summary>
    private async Task<Evaluation> EvaluateAsync(
        Guid merchantId, Guid assetId, Chain chain, BigInteger amount, Guid? settlementWalletId, CancellationToken cancellationToken)
    {
        Error? error = null;

        var merchant = await merchants.FindByIdAsync(merchantId, cancellationToken);
        if (merchant is null || !merchant.CanTransact)
            error = WithdrawalErrors.MerchantCannotTransact;

        // Destination: always a staff-approved settlement wallet — NEVER a free-form address (§10). The merchant may
        // pick WHICH approved wallet (the primary or a standby); with no pick it is the active (primary) one.
        ApprovedSettlementWallet? wallet;
        if (settlementWalletId is { } chosen)
        {
            wallet = await settlements.FindApprovedSettlementWalletAsync(merchantId, chain, chosen, cancellationToken);
            if (wallet is null)
                error ??= WithdrawalErrors.SettlementWalletNotUsable;
            // A directly designated address cannot be made the active wallet; the same rule stops it being picked
            // as a standby. Review/Unavailable pass here: every cash-out stops at admin audit, where staff see it.
            else if (string.Equals(wallet.ScreeningDecision, "Block", StringComparison.OrdinalIgnoreCase))
                error ??= WithdrawalErrors.SettlementWalletBlocked;
        }
        else
        {
            var address = await settlements.FindSettlementAddressAsync(merchantId, chain, cancellationToken);
            wallet = string.IsNullOrWhiteSpace(address) ? null : new ApprovedSettlementWallet(Guid.Empty, address, IsActive: true, null);
            if (wallet is null)
                error ??= WithdrawalErrors.SettlementWalletNotRegistered;
        }

        // A defensive format re-check (the primary check belongs at registration time) — catches a malformed wallet
        // before ever reserving funds against it, same reasoning as the user-payout path.
        if (wallet is not null && addressEncoders.Supports(chain) && !addressEncoders.For(chain).IsValidAddress(wallet.Address))
            error ??= WithdrawalErrors.DestinationInvalid;

        // Settled (withdrawable) balance — only funds matured past the merchant's T+N may cash out; also the base for
        // the percentage liquidity cap below. At T+0 this equals the full balance, so the settled reject is skipped
        // (the reserve remains the balance guard) and the cap is taken against the full balance. Best-effort
        // pre-check; the reserve stays the atomic overdraw guard (settled ≤ total).
        var settlementDelayDays = merchant?.SettlementDelayDays ?? 0;
        var available = await settledBalance.GetAvailableAsync(merchantId, assetId, cancellationToken);
        var settled = settlementDelayDays > 0
            ? await settledBalance.GetSettledAvailableAsync(merchantId, assetId, settlementDelayDays, cancellationToken)
            : available;
        if (settlementDelayDays > 0 && amount > settled)
            error ??= WithdrawalErrors.ExceedsSettledBalance;

        // Liquidity cap: min(flat, ⌊settled·bps/10000⌋) across whichever caps are set.
        var cap = await caps.GetAsync(merchantId, assetId, cancellationToken);
        BigInteger? limit = cap.HasCap ? EffectiveCap(cap, settled) : null;
        if (limit is { } max && amount > max)
            error ??= WithdrawalErrors.ExceedsMerchantWithdrawalLimit;

        // The same per-merchant withdrawal fee as a user payout — the merchant bears it on top of the amount; the
        // platform bears gas. The rate inputs travel with it onto the record (fee transparency).
        var fee = await feeSchedule.QuoteWithdrawalFeeAsync(merchantId, assetId, amount, cancellationToken);

        return new Evaluation(error, wallet, fee, available, settled, limit);
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
