using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

/// <summary>
/// A running balance for one account, maintained incrementally in the same transaction as each
/// journal posting. This is a <em>cache</em>, never the source of truth: it is fully rebuildable from
/// <see cref="JournalEntry"/> rows (§13 invariant 5). Its <c>Id</c> is the AccountId. Concurrency is
/// guarded by a native <c>rowversion</c> (a shadow property configured in the map).
/// </summary>
public sealed class AccountBalance : Entity<Guid>
{
    private AccountBalance(Guid accountId, BigInteger balance, Guid? lastEntryId, DateTimeOffset updatedAt) : base(accountId)
    {
        Balance = balance;
        LastEntryId = lastEntryId;
        UpdatedAt = updatedAt;
    }

    private AccountBalance() : base(Guid.Empty)
    {
    }

    /// <summary>Balance in the account's normal side, in base units. Non-negative by invariant.</summary>
    public BigInteger Balance { get; private set; }

    /// <summary>Watermark of the last entry folded in — lets a rebuild resume and detects double-apply.</summary>
    public Guid? LastEntryId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AccountBalance Open(Guid accountId, DateTimeOffset now) =>
        new(accountId, BigInteger.Zero, lastEntryId: null, now);

    /// <summary>
    /// Folds one entry into the running balance. A debit moves a debit-normal account up and a
    /// credit-normal account down (and vice-versa). Refuses to drive the balance below zero — a
    /// would-be-negative balance signals an upstream error (e.g. an over-reversal or an un-guarded
    /// overdraw) and must surface, not silently persist.
    /// </summary>
    public Result Apply(NormalSide normalSide, JournalEntry entry, DateTimeOffset now)
    {
        var signedDelta = normalSide == NormalSide.Debit
            ? entry.Debit - entry.Credit
            : entry.Credit - entry.Debit;

        var next = Balance + signedDelta;
        if (next < BigInteger.Zero)
            return Result.Failure(LedgerErrors.BalanceWouldGoNegative);

        Balance = next;
        LastEntryId = entry.Id;
        UpdatedAt = now;
        return Result.Success();
    }
}
