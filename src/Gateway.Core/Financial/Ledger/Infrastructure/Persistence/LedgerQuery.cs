using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

/// <summary>
/// Reads a merchant's available balance straight from the <c>AccountBalance</c> cache — the running
/// projection maintained in the same transaction as every posting, so it is always consistent with the
/// journal it is derived from. Read-only and untracked: it opens no account and posts nothing.
/// </summary>
public sealed class LedgerQuery(LedgerDbContext context) : ILedgerQuery
{
    public async Task<BigInteger> GetMerchantBalanceAsync(
        Guid merchantId, Guid assetId, CancellationToken cancellationToken = default)
    {
        // MerchantLiability(merchant, asset) is credit-normal; its cached balance is what we owe the
        // merchant right now. No account/balance row yet ⇒ the merchant has never transacted this asset
        // ⇒ zero (FirstOrDefaultAsync yields default(BigInteger), which is 0).
        return await (
            from account in context.Accounts.AsNoTracking()
            where account.AccountType == AccountType.MerchantLiability
               && account.OwnerType == OwnerType.Merchant
               && account.OwnerId == merchantId
               && account.AssetId == assetId
            join balance in context.AccountBalances.AsNoTracking() on account.Id equals balance.Id
            select balance.Balance)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<MerchantJournalView> Items, int TotalCount)> GetJournalsAsync(
        Guid? merchantId,
        Guid? referenceId,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var journalsQuery = context.Journals.AsNoTracking()
            // No merchant filter still excludes purely platform-internal journals (MerchantId == null) —
            // "all transactions" reads as "every merchant's activity," not an internal-postings firehose.
            .Where(j => merchantId == null ? j.MerchantId != null : j.MerchantId == merchantId)
            .Where(j => referenceId == null || j.ReferenceId == referenceId)
            .Where(j => fromDate == null || j.CreatedAt >= fromDate)
            .Where(j => toDate == null || j.CreatedAt <= toDate);

        var totalCount = await journalsQuery.CountAsync(cancellationToken);

        var journals = await journalsQuery
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        if (journals.Count == 0)
            return ([], totalCount);

        var journalIds = journals.Select(j => j.Id).ToList();

        // The merchant-liability line of each journal — the other lines (Treasury/Fee) are platform-internal.
        // Per-journal MerchantId (not the filter param, which may be null for an "all merchants" query) is
        // what identifies whose liability line to pick out of each journal's entries.
        var merchantIdsInPage = journals.Select(j => j.MerchantId).Where(id => id != null).Select(id => id!.Value).ToHashSet();

        var liabilityLines = await (
            from entry in context.JournalEntries.AsNoTracking()
            join account in context.Accounts.AsNoTracking() on entry.AccountId equals account.Id
            where journalIds.Contains(entry.JournalId)
               && account.AccountType == AccountType.MerchantLiability
               && account.OwnerId != null && merchantIdsInPage.Contains(account.OwnerId.Value)
            select entry)
            .ToListAsync(cancellationToken);

        var lineByJournal = liabilityLines.ToDictionary(e => e.JournalId);

        var items = journals
            .Select(j =>
            {
                var line = lineByJournal.GetValueOrDefault(j.Id);
                var direction = line is { IsDebit: true } ? EntryDirection.Debit : EntryDirection.Credit;
                var amount = line is null ? BigInteger.Zero : (line.IsDebit ? line.Debit : line.Credit);

                return new MerchantJournalView(
                    j.Id, j.ReferenceType.ToString(), j.ReferenceId, j.AssetId, j.Description,
                    direction.ToString(), amount, j.CreatedAt);
            })
            .ToList();

        return (items, totalCount);
    }
}
