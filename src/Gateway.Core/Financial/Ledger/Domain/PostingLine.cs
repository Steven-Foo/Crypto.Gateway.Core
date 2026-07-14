using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// The caller's request for one line of a journal, before it becomes an immutable <see cref="JournalEntry"/>.
/// <paramref name="Amount"/> is an unsigned base-unit magnitude; the <paramref name="Direction"/> says
/// which side it lands on.
/// </summary>
public readonly record struct PostingLine(Guid AccountId, EntryDirection Direction, BigInteger Amount)
{
    public static PostingLine Debit(Guid accountId, BigInteger amount) => new(accountId, EntryDirection.Debit, amount);
    public static PostingLine Credit(Guid accountId, BigInteger amount) => new(accountId, EntryDirection.Credit, amount);
}
