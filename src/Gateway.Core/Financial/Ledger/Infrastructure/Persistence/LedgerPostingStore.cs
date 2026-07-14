using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.Infrastructure.Locking;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

/// <summary>
/// Commits a balanced journal and its balance effects atomically and idempotently.
///
/// Correctness layers, strongest first:
/// <list type="number">
///   <item><b>UNIQUE (ReferenceType, ReferenceId)</b> — one journal per business event; a replay returns AlreadyPosted.</item>
///   <item><b>rowversion on AccountBalance</b> — two posters can't both apply a stale-read update; the loser retries.</item>
///   <item><b>Redis per-account lock</b> — single-flights posters to an account so retries are rare, not a correctness crutch.</item>
/// </list>
/// The transaction wraps journal + entries + balance updates: they commit together or not at all.
/// </summary>
public sealed class LedgerPostingStore(
    LedgerDbContext context,
    IDistributedLockFactory lockFactory,
    TimeProvider timeProvider,
    ILogger<LedgerPostingStore> logger) : ILedgerPostingStore
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public async Task<PostingOutcome> PostAsync(Journal journal, CancellationToken cancellationToken = default)
    {
        if (await ReferenceAlreadyPostedAsync(journal, cancellationToken))
            return PostingOutcome.AlreadyPosted;

        // Lock every affected account, in a stable order, so two postings that share accounts can never
        // deadlock each other.
        var accountIds = journal.Entries.Select(e => e.AccountId).Distinct().OrderBy(id => id).ToList();
        var handles = new List<IAsyncDisposable>(accountIds.Count);
        try
        {
            foreach (var accountId in accountIds)
                handles.Add(await lockFactory.AcquireAsync($"ledger:account:{accountId}", LockTimeout, cancellationToken));

            return await PostWithRetryAsync(journal, cancellationToken);
        }
        finally
        {
            for (var i = handles.Count - 1; i >= 0; i--)
                await handles[i].DisposeAsync();
        }
    }

    private async Task<PostingOutcome> PostWithRetryAsync(Journal journal, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Each attempt starts from a clean slate so freshly-read rowversions are used.
            context.ChangeTracker.Clear();

            var strategy = context.Database.CreateExecutionStrategy();
            try
            {
                return await strategy.ExecuteAsync(() => TryPostOnceAsync(journal, cancellationToken));
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                logger.LogWarning(
                    "Ledger posting for {ReferenceType}:{ReferenceId} hit a balance concurrency conflict; retry {Attempt}/{Max}.",
                    journal.ReferenceType, journal.ReferenceId, attempt, MaxAttempts);
                await BackoffAsync(attempt, cancellationToken);
            }
            catch (DbUpdateException ex) when (IsTransientUniqueRace(ex, journal) && attempt < MaxAttempts)
            {
                // A concurrent first-ever posting to a shared account inserted the AccountBalance row
                // between our check and our write. Reload and re-apply.
                logger.LogWarning(
                    "Ledger posting for {ReferenceType}:{ReferenceId} raced a balance insert; retry {Attempt}/{Max}.",
                    journal.ReferenceType, journal.ReferenceId, attempt, MaxAttempts);
                await BackoffAsync(attempt, cancellationToken);
            }
        }
    }

    private async Task<PostingOutcome> TryPostOnceAsync(Journal journal, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Re-check inside the transaction: a racer may have committed the same reference since the fast-path.
        if (await ReferenceAlreadyPostedAsync(journal, cancellationToken))
            return PostingOutcome.AlreadyPosted;

        context.Journals.Add(journal); // entries cascade

        foreach (var entry in journal.Entries)
        {
            var account = await context.Accounts.SingleAsync(a => a.Id == entry.AccountId, cancellationToken);
            if (!account.IsActive)
                throw new LedgerPostingException(LedgerErrors.AccountNotActive);
            if (account.AssetId != entry.AssetId)
                throw new LedgerPostingException(LedgerErrors.EntryAssetMismatch);

            var balance = await context.AccountBalances.SingleOrDefaultAsync(b => b.Id == account.Id, cancellationToken);
            if (balance is null)
            {
                balance = AccountBalance.Open(account.Id, timeProvider.GetUtcNow());
                context.AccountBalances.Add(balance);
            }

            var applied = balance.Apply(account.NormalSide, entry, timeProvider.GetUtcNow());
            if (applied.IsFailure)
                throw new LedgerPostingException(applied.Error!);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsJournalReplay(ex))
        {
            // The journal's own unique index tripped: another poster committed this exact reference.
            await transaction.RollbackAsync(cancellationToken);
            return PostingOutcome.AlreadyPosted;
        }

        await transaction.CommitAsync(cancellationToken);
        return PostingOutcome.Posted;
    }

    /// <summary>Jittered backoff so colliding posters spread out instead of live-locking on the same row.</summary>
    private static Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var jitterMs = Random.Shared.Next(5, 25) * attempt;
        return Task.Delay(jitterMs, cancellationToken);
    }

    private Task<bool> ReferenceAlreadyPostedAsync(Journal journal, CancellationToken cancellationToken) =>
        context.Journals
            .AsNoTracking()
            .AnyAsync(j => j.ReferenceType == journal.ReferenceType && j.ReferenceId == journal.ReferenceId, cancellationToken);

    private static bool IsJournalReplay(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_Journal_Reference", StringComparison.Ordinal);

    private static bool IsTransientUniqueRace(DbUpdateException ex, Journal journal) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } && !IsJournalReplay(ex);
}
