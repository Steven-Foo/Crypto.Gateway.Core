using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// One financial event, recorded as a balanced set of double-entry lines in a single asset. The
/// aggregate root of a posting: a journal is created <em>with</em> its complete, balanced set of
/// entries and validated as a unit — there is no API to append a line later, which is what makes it
/// atomic and immutable. Append-only; corrections are new compensating journals, never edits (§14).
///
/// Idempotency is the DB's job, not this type's: <c>(ReferenceType, ReferenceId)</c> is UNIQUE, so a
/// business event posts exactly one journal no matter how many times it is delivered (§7.3).
/// </summary>
public sealed class Journal : Entity<Guid>
{
    private readonly List<JournalEntry> _entries = [];

    private Journal(
        Guid id,
        JournalReferenceType referenceType,
        Guid referenceId,
        Guid assetId,
        Guid? merchantId,
        string description,
        DateTimeOffset createdAt) : base(id)
    {
        ReferenceType = referenceType;
        ReferenceId = referenceId;
        AssetId = assetId;
        MerchantId = merchantId;
        Description = description;
        CreatedAt = createdAt;
    }

    private Journal() : base(Guid.Empty)
    {
    }

    public JournalReferenceType ReferenceType { get; private set; }
    public Guid ReferenceId { get; private set; }

    /// <summary>The single asset every line of this journal is denominated in.</summary>
    public Guid AssetId { get; private set; }

    /// <summary>
    /// Denormalised reporting dimension: the merchant this event concerns (= the owner of the
    /// merchant-side line), or null for purely platform-internal events. Immutable, set once at posting.
    /// It is never a source of balance — merchant balances come from the liability account's entries —
    /// it only lets merchant statements and ops period-checks filter journals directly.
    /// </summary>
    public Guid? MerchantId { get; private set; }

    public string Description { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<JournalEntry> Entries => _entries;

    /// <summary>
    /// Builds and validates a balanced journal. Every structural money invariant is enforced here, so
    /// an unbalanced, single-line, wrong-side, or non-positive journal is unconstructable.
    /// Account existence/status/asset-match is the caller's responsibility (it owns the accounts).
    /// </summary>
    public static Result<Journal> Post(
        JournalReferenceType referenceType,
        Guid referenceId,
        Guid assetId,
        Guid? merchantId,
        string description,
        IReadOnlyCollection<PostingLine> lines,
        DateTimeOffset now)
    {
        if (referenceId == Guid.Empty)
            return Result.Failure<Journal>(LedgerErrors.ReferenceRequired);

        if (assetId == Guid.Empty)
            return Result.Failure<Journal>(LedgerErrors.AssetRequired);

        if (lines.Count < 2)
            return Result.Failure<Journal>(LedgerErrors.JournalNeedsTwoLines);

        var totalDebit = BigInteger.Zero;
        var totalCredit = BigInteger.Zero;

        foreach (var line in lines)
        {
            if (line.AccountId == Guid.Empty)
                return Result.Failure<Journal>(LedgerErrors.AccountRequired);

            if (line.Amount <= BigInteger.Zero)
                return Result.Failure<Journal>(LedgerErrors.NonPositiveAmount);

            if (!MoneyLimits.IsStorable(line.Amount))
                return Result.Failure<Journal>(LedgerErrors.NonPositiveAmount);

            if (line.Direction == EntryDirection.Debit)
                totalDebit += line.Amount;
            else
                totalCredit += line.Amount;
        }

        if (totalDebit != totalCredit)
            return Result.Failure<Journal>(LedgerErrors.Unbalanced);

        var journal = new Journal(Guid.CreateVersion7(), referenceType, referenceId, assetId, merchantId, description ?? string.Empty, now);
        foreach (var line in lines)
            journal._entries.Add(JournalEntry.Create(journal.Id, line, assetId, now));

        return Result.Success(journal);
    }
}
