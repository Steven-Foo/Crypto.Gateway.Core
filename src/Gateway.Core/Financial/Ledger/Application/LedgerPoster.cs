using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;

/// <summary>
/// Credit a confirmed deposit to a merchant. <paramref name="Amount"/> is the gross received (base units);
/// <paramref name="Fee"/> is the platform's deposit fee, taken off the top so the merchant is credited
/// <c>Amount − Fee</c> and the platform earns <c>Fee</c> (payer-on-top pricing). Fee defaults to zero.
/// </summary>
public sealed record CreditDepositCommand(Guid DepositId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee = default, string? Description = null);

/// <summary>
/// Reverse a previously-credited deposit that was orphaned by a reorg. Posts a compensating journal — never
/// edits. Must reverse the <em>same</em> split that was credited, so the caller passes the identical
/// <paramref name="Fee"/> (derived deterministically from the same confirmed amount).
/// </summary>
public sealed record ReverseDepositCommand(Guid DepositId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee = default, string? Description = null);

/// <summary>Settle a confirmed withdrawal: funds leave custody, the platform fee becomes revenue.</summary>
public sealed record SettleWithdrawalCommand(Guid WithdrawalId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee);

/// <summary>Release a rejected/failed withdrawal: reserved funds return to the merchant.</summary>
public sealed record ReleaseWithdrawalCommand(Guid WithdrawalId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee);

public interface ILedgerPoster
{
    Task<Result<PostingOutcome>> CreditDepositAsync(CreditDepositCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> ReverseDepositAsync(ReverseDepositCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> SettleWithdrawalAsync(SettleWithdrawalCommand command, CancellationToken cancellationToken = default);

    Task<Result<PostingOutcome>> ReleaseWithdrawalAsync(ReleaseWithdrawalCommand command, CancellationToken cancellationToken = default);
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
        var treasury = await accounts.GetOrCreateAsync(AccountType.TreasuryAsset, OwnerType.Treasury, null, command.AssetId, cancellationToken);

        var lines = new List<PostingLine>
        {
            PostingLine.Debit(clearing.Id, total),
            PostingLine.Credit(treasury.Id, command.Amount),
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
            JournalReferenceType.Deposit,
            command.DepositId,
            command.MerchantId,
            command.AssetId,
            command.Amount,
            command.Fee,
            command.Description ?? "Deposit credit",
            credit: true,
            cancellationToken);

    public Task<Result<PostingOutcome>> ReverseDepositAsync(ReverseDepositCommand command, CancellationToken cancellationToken = default) =>
        PostDepositAsync(
            JournalReferenceType.DepositReversal,
            command.DepositId,
            command.MerchantId,
            command.AssetId,
            command.Amount,
            command.Fee,
            command.Description ?? "Deposit reversal (reorg/orphan)",
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
