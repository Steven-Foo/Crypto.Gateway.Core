using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;

/// <summary>The result of attempting to post a journal.</summary>
public enum PostingOutcome
{
    /// <summary>The journal was appended and balances updated in this call.</summary>
    Posted = 1,

    /// <summary>
    /// A journal for the same <c>(ReferenceType, ReferenceId)</c> already existed — a replay. No
    /// second credit was made; the ledger was already correct. Treated as success (§7.3).
    /// </summary>
    AlreadyPosted = 2,
}

/// <summary>
/// Atomically appends a balanced journal and folds its entries into the affected account balances,
/// in a single transaction. Guarantees:
/// <list type="bullet">
///   <item>journal + entries + balance updates commit together, or not at all;</item>
///   <item>idempotent on <c>(ReferenceType, ReferenceId)</c> — a replay returns <see cref="PostingOutcome.AlreadyPosted"/>;</item>
///   <item>each affected account is single-flighted (distributed lock) and its balance row is guarded by a rowversion.</item>
/// </list>
/// The concurrency machinery lives in Infrastructure; the Application only hands over a valid journal.
/// </summary>
public interface ILedgerPostingStore
{
    Task<PostingOutcome> PostAsync(Journal journal, CancellationToken cancellationToken = default);
}
