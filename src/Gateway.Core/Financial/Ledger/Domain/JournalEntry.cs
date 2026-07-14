using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// One line of a <see cref="Journal"/>: a debit XOR a credit against one account, in one asset.
/// Append-only and immutable — never updated, never deleted. Amounts are unsigned base units
/// (<see cref="BigInteger"/>); direction is carried by which column is non-zero, never by a sign (§14).
/// Only <see cref="Journal"/> can create one, so a line can never exist outside a balanced journal.
/// </summary>
public sealed class JournalEntry : Entity<Guid>
{
    private JournalEntry(
        Guid id, Guid journalId, Guid accountId, Guid assetId, BigInteger debit, BigInteger credit, DateTimeOffset createdAt)
        : base(id)
    {
        JournalId = journalId;
        AccountId = accountId;
        AssetId = assetId;
        Debit = debit;
        Credit = credit;
        CreatedAt = createdAt;
    }

    private JournalEntry() : base(Guid.Empty)
    {
    }

    public Guid JournalId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid AssetId { get; private set; }
    public BigInteger Debit { get; private set; }
    public BigInteger Credit { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsDebit => Debit > BigInteger.Zero;

    internal static JournalEntry Create(Guid journalId, PostingLine line, Guid assetId, DateTimeOffset createdAt)
    {
        var (debit, credit) = line.Direction == EntryDirection.Debit
            ? (line.Amount, BigInteger.Zero)
            : (BigInteger.Zero, line.Amount);

        return new JournalEntry(Guid.CreateVersion7(), journalId, line.AccountId, assetId, debit, credit, createdAt);
    }
}
