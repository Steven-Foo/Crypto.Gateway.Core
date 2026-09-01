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

    public async Task<BigInteger> GetMerchantSettledBalanceAsync(
        Guid merchantId, Guid assetId, DateTimeOffset unmaturedCutoffUtc, CancellationToken cancellationToken = default)
    {
        var total = await GetMerchantBalanceAsync(merchantId, assetId, cancellationToken);

        // Still-maturing deposit inflow: the merchant-liability lines of Deposit / DepositReversal journals
        // dated on/after the cutoff. Subtracting their net from the authoritative cache balance yields the
        // settled (withdrawable) amount, with releases/reversals/fees already handled by the cache; a deposit
        // and its reorg-reversal (both recent) net to zero. Pulled and summed in memory because the money
        // columns are BigInteger (decimal(38,0) via a custom mapping) — SUM is not provider-translatable — and
        // the window is bounded to one merchant's deposits over the settlement period, an infrequent read.
        var unmaturedLines = await (
            from account in context.Accounts.AsNoTracking()
            where account.AccountType == AccountType.MerchantLiability
               && account.OwnerType == OwnerType.Merchant
               && account.OwnerId == merchantId
               && account.AssetId == assetId
            join entry in context.JournalEntries.AsNoTracking() on account.Id equals entry.AccountId
            join journal in context.Journals.AsNoTracking() on entry.JournalId equals journal.Id
            // DELIBERATELY only the two customer-deposit types. A merchant top-up posts under
            // MerchantTopUp / MerchantTopUpReversal and so falls outside this filter, which is exactly what
            // makes it withdrawable immediately (T+N-exempt) — the merchant is funding its own balance, not
            // receiving a customer payment. Do NOT add the top-up types here "for completeness": that would
            // silently re-impose the settlement delay on top-ups. Guarded by
            // LedgerQueryTests.A_merchant_top_up_is_settled_immediately_while_a_customer_deposit_is_not.
            where (journal.ReferenceType == JournalReferenceType.Deposit
                || journal.ReferenceType == JournalReferenceType.DepositReversal)
               && journal.CreatedAt >= unmaturedCutoffUtc
            select new { entry.Debit, entry.Credit })
            .ToListAsync(cancellationToken);

        var unmaturedNet = unmaturedLines.Aggregate(
            BigInteger.Zero, (sum, line) => sum + line.Credit - line.Debit);

        var settled = total - unmaturedNet;
        return settled > BigInteger.Zero ? settled : BigInteger.Zero;
    }

    public async Task<BigInteger> GetTreasuryHoldingAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        // TreasuryAsset(asset) is the debit-normal custody account (OwnerType.Treasury, no owner id); its
        // cached balance is the total we custody on-chain for the asset right now. No account/balance row yet
        // ⇒ the asset has never been custodied ⇒ zero (FirstOrDefaultAsync yields default(BigInteger) = 0).
        return await (
            from account in context.Accounts.AsNoTracking()
            where account.AccountType == AccountType.TreasuryAsset
               && account.OwnerType == OwnerType.Treasury
               && account.OwnerId == null
               && account.AssetId == assetId
            join balance in context.AccountBalances.AsNoTracking() on account.Id equals balance.Id
            select balance.Balance)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<BigInteger> GetWithdrawalWalletTopUpTotalAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SystemAccountBalanceAsync(AccountType.WithdrawalWalletTopUp, assetId, cancellationToken);

    public Task<BigInteger> GetExternalSettlementTotalAsync(Guid assetId, CancellationToken cancellationToken = default) =>
        SystemAccountBalanceAsync(AccountType.ExternalSettlement, assetId, cancellationToken);

    /// <summary>A System-owned account balance for one asset. No row yet ⇒ nothing posted ⇒ zero.</summary>
    private async Task<BigInteger> SystemAccountBalanceAsync(AccountType type, Guid assetId, CancellationToken cancellationToken)
    {
        return await (
            from account in context.Accounts.AsNoTracking()
            where account.AccountType == type
               && account.OwnerType == OwnerType.System
               && account.OwnerId == null
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

    public async Task<(IReadOnlyList<MerchantBalanceChangeView> Items, int TotalCount)> GetMerchantBalanceHistoryAsync(
        Guid merchantId,
        Guid? assetId,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // INNER JOIN on the merchant's own MerchantLiability account line — the money-critical difference
        // from GetJournalsAsync: a journal that carries this merchant's id but never posted a line against
        // their liability account (WithdrawalSettle) simply has no matching row here, never a zero-amount
        // placeholder.
        var query =
            from entry in context.JournalEntries.AsNoTracking()
            join account in context.Accounts.AsNoTracking() on entry.AccountId equals account.Id
            join journal in context.Journals.AsNoTracking() on entry.JournalId equals journal.Id
            where account.AccountType == AccountType.MerchantLiability
               && account.OwnerType == OwnerType.Merchant
               && account.OwnerId == merchantId
               && (assetId == null || journal.AssetId == assetId)
               && (fromDate == null || journal.CreatedAt >= fromDate)
               && (toDate == null || journal.CreatedAt <= toDate)
            select new { journal, entry };

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(x => x.journal.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x => new MerchantBalanceChangeView(
                x.journal.Id,
                x.journal.ReferenceType.ToString(),
                x.journal.ReferenceId,
                x.journal.AssetId,
                x.journal.Description,
                (x.entry.IsDebit ? EntryDirection.Debit : EntryDirection.Credit).ToString(),
                x.entry.IsDebit ? x.entry.Debit : x.entry.Credit,
                x.journal.CreatedAt))
            .ToList();

        return (items, totalCount);
    }
}
