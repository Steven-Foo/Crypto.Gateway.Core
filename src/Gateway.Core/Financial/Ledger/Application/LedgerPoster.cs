using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;

/// <summary>
/// Credit a confirmed deposit to a merchant. <paramref name="Amount"/> is the gross received (base units);
/// <paramref name="Fee"/> is the platform's fee, deducted from what arrived so the merchant is credited
/// <c>Amount − Fee</c> and the platform earns <c>Fee</c>. The payer is never charged more than the invoice
/// states. Fee defaults to zero. <paramref name="IsTopUp"/> selects the journal reference type, which is what
/// exempts a merchant top-up from the T+N settlement hold (see <c>LedgerQuery</c>).
/// </summary>
public sealed record CreditDepositCommand(Guid DepositId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee = default, string? Description = null, bool IsTopUp = false);

/// <summary>
/// Reverse a previously-credited deposit that was orphaned by a reorg. Posts a compensating journal — never
/// edits. Must reverse the <em>same</em> split that was credited, so the caller passes the identical
/// <paramref name="Fee"/> (derived deterministically from the same confirmed amount).
/// </summary>
public sealed record ReverseDepositCommand(Guid DepositId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee = default, string? Description = null, bool IsTopUp = false);

/// <summary>
/// Settle a confirmed withdrawal: the merchant's reserved funds are discharged and the platform fee becomes
/// revenue.
///
/// <para><paramref name="ExternallySettled"/> selects which account absorbs the amount, and it is the one
/// field here that must not be got wrong. False (the default, and every automated payout) means the platform
/// paid from its own hot wallet, so <c>TreasuryAsset</c> is credited — custody genuinely fell. True means an
/// operations admin paid the merchant from a company wallet outside platform custody, so
/// <c>ExternalSettlement</c> is credited instead and custody is left alone: no watched address was debited,
/// and crediting TreasuryAsset would drift reconciliation downward by every such settlement (§14).</para>
/// </summary>
public sealed record SettleWithdrawalCommand(
    Guid WithdrawalId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee, bool ExternallySettled = false);

/// <summary>
/// Record company funds moved into a hot withdrawal wallet to keep the payout pipeline funded.
/// <paramref name="TopUpId"/> is the recorded top-up's id — the idempotency key, so re-posting the same
/// recorded transfer is a no-op. The transfer itself has already happened on-chain and been verified; this
/// only books it.
/// </summary>
public sealed record RecordTopUpCommand(Guid TopUpId, Guid AssetId, BigInteger Amount, string? Description = null);

/// <summary>Release a rejected/failed withdrawal: reserved funds return to the merchant.</summary>
public sealed record ReleaseWithdrawalCommand(Guid WithdrawalId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee);

/// <summary>
/// A staff-initiated manual credit to a merchant's balance — NOT backed by a real on-chain deposit, so it
/// must never touch <see cref="AccountType.TreasuryAsset"/> (Reconciliation compares that against real
/// on-chain balances; a manual credit posted there would create a permanent false drift). <paramref name="Reason"/>
/// is mandatory (enforced by the caller — an Ops endpoint) and lands in the journal description for audit.
/// <paramref name="AdjustmentId"/> is the idempotency key with <see cref="JournalReferenceType.Adjustment"/>;
/// omit it to mint a fresh one, or supply a stable value so a retried staff action replays safely instead of
/// double-posting.
/// </summary>
public sealed record CreditMerchantBalanceCommand(Guid MerchantId, Guid AssetId, BigInteger Amount, string Reason, Guid? AdjustmentId = null);

/// <summary>
/// The mirror of <see cref="CreditMerchantBalanceCommand"/>: a staff-initiated manual debit. Guarded by the
/// exact same atomic negative-balance check a withdrawal reserve uses — it can never overdraw the merchant,
/// regardless of concurrent activity.
/// </summary>
public sealed record DebitMerchantBalanceCommand(Guid MerchantId, Guid AssetId, BigInteger Amount, string Reason, Guid? AdjustmentId = null);

/// <summary>
/// Book the native-coin (gas/energy) cost the platform bore for one on-chain operation. <paramref name="ReferenceId"/>
/// is the operation's id (e.g. the withdrawal id) — the idempotency key with <c>GasCost</c>. <paramref name="GasAssetId"/>
/// denominates the fee (TRX for TRON); <paramref name="FeeSun"/> is the fee in that asset's base units.
/// <paramref name="ReferenceType"/> is a human label for the journal description (e.g. "Withdrawal").
/// </summary>
public sealed record RecordGasSpentCommand(Guid ReferenceId, string ReferenceType, Guid GasAssetId, BigInteger FeeSun, string? Description = null);

public interface ILedgerPoster
{
    Task<Result<PostingOutcome>> CreditDepositAsync(CreditDepositCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> ReverseDepositAsync(ReverseDepositCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> SettleWithdrawalAsync(SettleWithdrawalCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> ReleaseWithdrawalAsync(ReleaseWithdrawalCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> RecordGasSpentAsync(RecordGasSpentCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> RecordTopUpAsync(RecordTopUpCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> CreditMerchantBalanceAsync(CreditMerchantBalanceCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> DebitMerchantBalanceAsync(DebitMerchantBalanceCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single write path into the ledger. It owns the <em>accounting policy</em> — which accounts a
/// business event debits and credits — and nothing else: it builds a balanced <see cref="Journal"/>
/// via the domain and hands it to the posting store to commit atomically and idempotently.
///
/// Deposit accounting: custody rises and our obligation to the merchant rises by the same amount.
/// <code>
///   DEBIT  TreasuryAsset(asset)          amount   (asset ↑ on debit)
///   CREDIT MerchantLiability(merchant)   amount   (liability ↑ on credit)
/// </code>
/// A reversal is the mirror image, posted as a new journal keyed <c>(DepositReversal, depositId)</c>.
/// </summary>
public sealed class LedgerPoster(
    ILedgerAccountStore accounts,
    ILedgerPostingStore postingStore,
    TimeProvider timeProvider) : ILedgerPoster, IWithdrawalLedger
{
    // ── Withdrawal money-out ──────────────────────────────────────────────────────

    /// <summary>
    /// Reserve (synchronous, the balance check): DEBIT MerchantLiability, CREDIT WithdrawalClearing by
    /// amount+fee. The negative-balance guard rejects an unaffordable withdrawal atomically — no overdraw,
    /// no race between concurrent requests.
    /// </summary>
    public async Task<Result> ReserveAsync(ReserveWithdrawalRequest request, CancellationToken cancellationToken = default)
    {
        var total = request.Amount + request.Fee;
        if (request.Amount <= BigInteger.Zero || request.Fee < BigInteger.Zero || !MoneyLimits.IsStorable(total))
            return Result.Failure(LedgerErrors.NonPositiveAmount);

        var liability = await accounts.GetOrCreateAsync(AccountType.MerchantLiability, OwnerType.Merchant, request.MerchantId, request.AssetId, cancellationToken);
        var clearing = await accounts.GetOrCreateAsync(AccountType.WithdrawalClearing, OwnerType.System, null, request.AssetId, cancellationToken);

        var journal = Journal.Post(
            JournalReferenceType.WithdrawalReserve, request.WithdrawalId, request.AssetId, request.MerchantId,
            "Withdrawal reserve",
            [PostingLine.Debit(liability.Id, total), PostingLine.Credit(clearing.Id, total)],
            timeProvider.GetUtcNow());

        if (journal.IsFailure)
            return Result.Failure(journal.Error!);

        try
        {
            await postingStore.PostAsync(journal.Value, cancellationToken);
            return Result.Success();
        }
        catch (LedgerPostingException ex) when (ex.Error.Code == LedgerErrors.BalanceWouldGoNegative.Code)
        {
            // Insufficient funds is an expected business outcome here (unlike anywhere else), so translate
            // the guard into a Result instead of letting it surface as an incident.
            return Result.Failure(LedgerErrors.InsufficientBalance);
        }
    }

    /// <summary>
    /// Settle a confirmed withdrawal: DEBIT WithdrawalClearing amount+fee; CREDIT TreasuryAsset amount
    /// (custody drops by what left the chain); CREDIT FeeRevenue fee (the platform keeps the fee).
    /// </summary>
    public async Task<Result<PostingOutcome>> SettleWithdrawalAsync(SettleWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var total = command.Amount + command.Fee;
        if (command.Amount <= BigInteger.Zero || command.Fee < BigInteger.Zero || !MoneyLimits.IsStorable(total))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var clearing = await accounts.GetOrCreateAsync(AccountType.WithdrawalClearing, OwnerType.System, null, command.AssetId, cancellationToken);

        // WHICH account absorbs the amount depends on whether OUR custody actually paid it.
        //   Automated payout  → the platform's own hot wallet was debited on-chain ⇒ credit TreasuryAsset
        //                       (custody genuinely fell, and reconciliation must see that).
        //   Finance settlement → an admin paid from a company wallet outside platform custody ⇒ credit
        //                       ExternalSettlement. No watched address moved, so touching TreasuryAsset here
        //                       would decrement custody that never left, drifting reconciliation downward by
        //                       every settlement ever made (§14).
        // The merchant side is identical either way: the reserve is discharged and the fee earned.
        var counterparty = command.ExternallySettled
            ? await accounts.GetOrCreateAsync(AccountType.ExternalSettlement, OwnerType.System, null, command.AssetId, cancellationToken)
            : await accounts.GetOrCreateAsync(AccountType.TreasuryAsset, OwnerType.Treasury, null, command.AssetId, cancellationToken);

        var lines = new List<PostingLine>
        {
            PostingLine.Debit(clearing.Id, total),
            PostingLine.Credit(counterparty.Id, command.Amount),
        };

        if (command.Fee > BigInteger.Zero)
        {
            var feeRevenue = await accounts.GetOrCreateAsync(AccountType.FeeRevenue, OwnerType.System, null, command.AssetId, cancellationToken);
            lines.Add(PostingLine.Credit(feeRevenue.Id, command.Fee));
        }

        return await PostAsync(JournalReferenceType.WithdrawalSettle, command.WithdrawalId, command.AssetId, command.MerchantId, "Withdrawal settlement", lines, cancellationToken);
    }

    /// <summary>
    /// Release a rejected/failed withdrawal: the mirror of reserve — DEBIT WithdrawalClearing, CREDIT
    /// MerchantLiability by amount+fee. The merchant gets their spendable balance back.
    /// </summary>
    public async Task<Result<PostingOutcome>> ReleaseWithdrawalAsync(ReleaseWithdrawalCommand command, CancellationToken cancellationToken = default)
    {
        var total = command.Amount + command.Fee;
        if (command.Amount <= BigInteger.Zero || command.Fee < BigInteger.Zero || !MoneyLimits.IsStorable(total))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var clearing = await accounts.GetOrCreateAsync(AccountType.WithdrawalClearing, OwnerType.System, null, command.AssetId, cancellationToken);
        var liability = await accounts.GetOrCreateAsync(AccountType.MerchantLiability, OwnerType.Merchant, command.MerchantId, command.AssetId, cancellationToken);

        List<PostingLine> lines =
        [
            PostingLine.Debit(clearing.Id, total),
            PostingLine.Credit(liability.Id, total),
        ];

        return await PostAsync(JournalReferenceType.WithdrawalRelease, command.WithdrawalId, command.AssetId, command.MerchantId, "Withdrawal release", lines, cancellationToken);
    }

    /// <summary>
    /// Book a platform gas cost: DEBIT NetworkFeeExpense (the expense grows), CREDIT PlatformFunding (the
    /// equity-like source it's drawn from) by the fee — a platform journal with no merchant line. Both accounts
    /// are System-owned and denominated in the gas asset. Idempotent, keyed <c>(GasCost, referenceId)</c>, so a
    /// replayed confirmation event never double-books. A zero/negative fee is a no-op success (dev's in-memory
    /// engine reports no fee, so no journal is written there).
    /// </summary>
    public async Task<Result<PostingOutcome>> RecordGasSpentAsync(RecordGasSpentCommand command, CancellationToken cancellationToken = default)
    {
        if (command.FeeSun <= BigInteger.Zero)
            return Result.Success(PostingOutcome.NoChange);

        if (!MoneyLimits.IsStorable(command.FeeSun))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var expense = await accounts.GetOrCreateAsync(AccountType.NetworkFeeExpense, OwnerType.System, null, command.GasAssetId, cancellationToken);
        var funding = await accounts.GetOrCreateAsync(AccountType.PlatformFunding, OwnerType.System, null, command.GasAssetId, cancellationToken);

        List<PostingLine> lines =
        [
            PostingLine.Debit(expense.Id, command.FeeSun),
            PostingLine.Credit(funding.Id, command.FeeSun),
        ];

        var description = command.Description ?? $"Gas cost ({command.ReferenceType})";
        return await PostAsync(JournalReferenceType.GasCost, command.ReferenceId, command.GasAssetId, merchantId: null, description, lines, cancellationToken);
    }

    /// <summary>
    /// Book company funds moved into a hot withdrawal wallet: DEBIT TreasuryAsset (we now hold more crypto in
    /// an address we watch — without this, reconciliation would report drift equal to every top-up ever made);
    /// CREDIT WithdrawalWalletTopUp (a System-owned contribution account).
    ///
    /// <para>The credit deliberately does NOT touch any merchant account. A top-up is operating liquidity, not
    /// merchant money, so it must leave <c>merchant withdrawable = deposits − fees − settlements − payouts</c>
    /// exactly as it was — that invariant holds here by construction, not by convention (§14).</para>
    /// </summary>
    public async Task<Result<PostingOutcome>> RecordTopUpAsync(RecordTopUpCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Amount <= BigInteger.Zero || !MoneyLimits.IsStorable(command.Amount))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var treasury = await accounts.GetOrCreateAsync(AccountType.TreasuryAsset, OwnerType.Treasury, null, command.AssetId, cancellationToken);
        var topUp = await accounts.GetOrCreateAsync(AccountType.WithdrawalWalletTopUp, OwnerType.System, null, command.AssetId, cancellationToken);

        List<PostingLine> lines =
        [
            PostingLine.Debit(treasury.Id, command.Amount),
            PostingLine.Credit(topUp.Id, command.Amount),
        ];

        var description = command.Description ?? "Hot withdrawal wallet top-up";
        return await PostAsync(
            JournalReferenceType.WithdrawalWalletTopUp, command.TopUpId, command.AssetId, merchantId: null,
            description, lines, cancellationToken);
    }

    /// <summary>
    /// Manual credit: DEBIT ManualAdjustmentCredit (a one-directional, non-reconciled bucket — never
    /// TreasuryAsset), CREDIT MerchantLiability. Idempotent on <c>(Adjustment, adjustmentId)</c> — a retried
    /// staff action with the same id safely no-ops instead of double-crediting. Split from the debit side's
    /// account (<see cref="AccountType.ManualAdjustmentDebit"/>) so this account only ever grows from zero and
    /// can never itself trip the negative-balance guard.
    /// </summary>
    public async Task<Result<PostingOutcome>> CreditMerchantBalanceAsync(CreditMerchantBalanceCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Amount <= BigInteger.Zero || !MoneyLimits.IsStorable(command.Amount))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var adjustment = await accounts.GetOrCreateAsync(AccountType.ManualAdjustmentCredit, OwnerType.System, null, command.AssetId, cancellationToken);
        var liability = await accounts.GetOrCreateAsync(AccountType.MerchantLiability, OwnerType.Merchant, command.MerchantId, command.AssetId, cancellationToken);

        List<PostingLine> lines =
        [
            PostingLine.Debit(adjustment.Id, command.Amount),
            PostingLine.Credit(liability.Id, command.Amount),
        ];

        return await PostAsync(
            JournalReferenceType.Adjustment, command.AdjustmentId ?? Guid.CreateVersion7(), command.AssetId, command.MerchantId,
            $"Manual credit: {command.Reason}", lines, cancellationToken);
    }

    /// <summary>
    /// Manual debit: the mirror of <see cref="CreditMerchantBalanceAsync"/> — DEBIT MerchantLiability, CREDIT
    /// ManualAdjustmentDebit (its own one-directional bucket, distinct from the credit side's — so how much
    /// has ever been manually credited can never gate whether a debit is allowed; only the merchant's own
    /// liability balance does). The negative-balance guard (same mechanism a withdrawal reserve relies on)
    /// rejects an overdraw atomically; translated to a clean
    /// <see cref="LedgerErrors.InsufficientBalanceForAdjustment"/> rather than surfacing as an incident.
    /// </summary>
    public async Task<Result<PostingOutcome>> DebitMerchantBalanceAsync(DebitMerchantBalanceCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Amount <= BigInteger.Zero || !MoneyLimits.IsStorable(command.Amount))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var adjustment = await accounts.GetOrCreateAsync(AccountType.ManualAdjustmentDebit, OwnerType.System, null, command.AssetId, cancellationToken);
        var liability = await accounts.GetOrCreateAsync(AccountType.MerchantLiability, OwnerType.Merchant, command.MerchantId, command.AssetId, cancellationToken);

        List<PostingLine> lines =
        [
            PostingLine.Debit(liability.Id, command.Amount),
            PostingLine.Credit(adjustment.Id, command.Amount),
        ];

        var journal = Journal.Post(
            JournalReferenceType.Adjustment, command.AdjustmentId ?? Guid.CreateVersion7(), command.AssetId, command.MerchantId,
            $"Manual debit: {command.Reason}", lines, timeProvider.GetUtcNow());

        if (journal.IsFailure)
            return Result.Failure<PostingOutcome>(journal.Error!);

        try
        {
            var outcome = await postingStore.PostAsync(journal.Value, cancellationToken);
            return Result.Success(outcome);
        }
        catch (LedgerPostingException ex) when (ex.Error.Code == LedgerErrors.BalanceWouldGoNegative.Code)
        {
            return Result.Failure<PostingOutcome>(LedgerErrors.InsufficientBalanceForAdjustment);
        }
    }

    private async Task<Result<PostingOutcome>> PostAsync(
        JournalReferenceType referenceType, Guid referenceId, Guid assetId, Guid? merchantId, string description,
        IReadOnlyCollection<PostingLine> lines, CancellationToken cancellationToken)
    {
        var journal = Journal.Post(referenceType, referenceId, assetId, merchantId, description, lines, timeProvider.GetUtcNow());
        if (journal.IsFailure)
            return Result.Failure<PostingOutcome>(journal.Error!);

        var outcome = await postingStore.PostAsync(journal.Value, cancellationToken);
        return Result.Success(outcome);
    }

    // ── Deposit money-in ──────────────────────────────────────────────────────────

    public Task<Result<PostingOutcome>> CreditDepositAsync(CreditDepositCommand command, CancellationToken cancellationToken = default) =>
        PostDepositAsync(
            command.IsTopUp ? JournalReferenceType.MerchantTopUp : JournalReferenceType.Deposit,
            command.DepositId,
            command.MerchantId,
            command.AssetId,
            command.Amount,
            command.Fee,
            command.Description ?? (command.IsTopUp ? "Merchant top-up credit" : "Deposit credit"),
            credit: true,
            cancellationToken);

    public Task<Result<PostingOutcome>> ReverseDepositAsync(ReverseDepositCommand command, CancellationToken cancellationToken = default) =>
        PostDepositAsync(
            command.IsTopUp ? JournalReferenceType.MerchantTopUpReversal : JournalReferenceType.DepositReversal,
            command.DepositId,
            command.MerchantId,
            command.AssetId,
            command.Amount,
            command.Fee,
            command.Description ?? (command.IsTopUp ? "Merchant top-up reversal (reorg/orphan)" : "Deposit reversal (reorg/orphan)"),
            credit: false,
            cancellationToken);

    /// <summary>
    /// Deposit money-in, fee taken off the top: DEBIT TreasuryAsset by the gross received; CREDIT
    /// MerchantLiability by the net (gross − fee); CREDIT FeeRevenue by the fee. A reversal is the exact
    /// mirror. When <paramref name="fee"/> is zero the FeeRevenue line is omitted, collapsing to the
    /// original two-line journal (backward compatible). A dust deposit smaller than the fixed fee is
    /// entirely consumed (net 0, no liability line) rather than crediting a negative balance.
    /// </summary>
    private async Task<Result<PostingOutcome>> PostDepositAsync(
        JournalReferenceType referenceType,
        Guid depositId,
        Guid merchantId,
        Guid assetId,
        BigInteger gross,
        BigInteger fee,
        string description,
        bool credit,
        CancellationToken cancellationToken)
    {
        if (gross <= BigInteger.Zero || !MoneyLimits.IsStorable(gross))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        // The fee can never exceed the deposit (that would credit a negative balance) nor be negative.
        var effectiveFee = BigInteger.Max(BigInteger.Zero, BigInteger.Min(fee, gross));
        var net = gross - effectiveFee;

        var treasury = await accounts.GetOrCreateAsync(AccountType.TreasuryAsset, OwnerType.Treasury, null, assetId, cancellationToken);
        var liability = await accounts.GetOrCreateAsync(AccountType.MerchantLiability, OwnerType.Merchant, merchantId, assetId, cancellationToken);

        // Credit: DEBIT treasury (gross), CREDIT merchant (net) + fee revenue (fee). Reversal: the mirror.
        var treasuryLine = credit ? PostingLine.Debit(treasury.Id, gross) : PostingLine.Credit(treasury.Id, gross);
        var lines = new List<PostingLine> { treasuryLine };

        if (net > BigInteger.Zero)
            lines.Add(credit ? PostingLine.Credit(liability.Id, net) : PostingLine.Debit(liability.Id, net));

        if (effectiveFee > BigInteger.Zero)
        {
            var feeRevenue = await accounts.GetOrCreateAsync(AccountType.FeeRevenue, OwnerType.System, null, assetId, cancellationToken);
            lines.Add(credit ? PostingLine.Credit(feeRevenue.Id, effectiveFee) : PostingLine.Debit(feeRevenue.Id, effectiveFee));
        }

        var journal = Journal.Post(
            referenceType, depositId, assetId, merchantId, description, lines, timeProvider.GetUtcNow());

        if (journal.IsFailure)
            return Result.Failure<PostingOutcome>(journal.Error!);

        var outcome = await postingStore.PostAsync(journal.Value, cancellationToken);
        return Result.Success(outcome);
    }
}
