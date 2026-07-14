using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;

/// <summary>Credit a confirmed deposit to a merchant. Amount is unsigned base units.</summary>
public sealed record CreditDepositCommand(Guid DepositId, Guid MerchantId, Guid AssetId, BigInteger Amount, string? Description = null);

/// <summary>Reverse a previously-credited deposit that was orphaned by a reorg. Posts a compensating journal — never edits.</summary>
public sealed record ReverseDepositCommand(Guid DepositId, Guid MerchantId, Guid AssetId, BigInteger Amount, string? Description = null);

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
            command.Description ?? "Deposit credit",
            debitTreasury: true,
            cancellationToken);

    public Task<Result<PostingOutcome>> ReverseDepositAsync(ReverseDepositCommand command, CancellationToken cancellationToken = default) =>
        PostDepositAsync(
            JournalReferenceType.DepositReversal,
            command.DepositId,
            command.MerchantId,
            command.AssetId,
            command.Amount,
            command.Description ?? "Deposit reversal (reorg/orphan)",
            debitTreasury: false,
            cancellationToken);

    private async Task<Result<PostingOutcome>> PostDepositAsync(
        JournalReferenceType referenceType,
        Guid depositId,
        Guid merchantId,
        Guid assetId,
        BigInteger amount,
        string description,
        bool debitTreasury,
        CancellationToken cancellationToken)
    {
        if (amount <= BigInteger.Zero || !MoneyLimits.IsStorable(amount))
            return Result.Failure<PostingOutcome>(LedgerErrors.NonPositiveAmount);

        var treasury = await accounts.GetOrCreateAsync(AccountType.TreasuryAsset, OwnerType.Treasury, null, assetId, cancellationToken);
        var liability = await accounts.GetOrCreateAsync(AccountType.MerchantLiability, OwnerType.Merchant, merchantId, assetId, cancellationToken);

        // Credit: debit treasury, credit merchant. Reversal: the mirror.
        PostingLine[] lines = debitTreasury
            ?
            [
                PostingLine.Debit(treasury.Id, amount),
                PostingLine.Credit(liability.Id, amount),
            ]
            :
            [
                PostingLine.Debit(liability.Id, amount),
                PostingLine.Credit(treasury.Id, amount),
            ];

        var journal = Journal.Post(
            referenceType, depositId, assetId, merchantId, description, lines, timeProvider.GetUtcNow());

        if (journal.IsFailure)
            return Result.Failure<PostingOutcome>(journal.Error!);

        var outcome = await postingStore.PostAsync(journal.Value, cancellationToken);
        return Result.Success(outcome);
    }
}
