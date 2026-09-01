using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using Microsoft.EntityFrameworkCore;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalDirectory(WithdrawalDbContext context) : IWithdrawalDirectory
{
    public async Task<WithdrawalView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, string kind = "User", CancellationToken cancellationToken = default)
    {
        // The (merchant, kind, reference) idempotency key means a user payout and a merchant cash-out can share
        // one reference, so the lookup is scoped to a single kind — never a multiple-match. An unrecognised kind
        // falls back to User (the host validates it upstream, so this is just defensive).
        var withdrawalKind = Enum.TryParse<WithdrawalKind>(kind, ignoreCase: true, out var parsed)
            ? parsed
            : WithdrawalKind.User;

        var withdrawal = await context.Withdrawals.AsNoTracking()
            .SingleOrDefaultAsync(
                w => w.MerchantId == merchantId && w.Kind == withdrawalKind && w.MerchantTransactionId == merchantTransactionId,
                cancellationToken);

        if (withdrawal is null)
            return null;

        return new WithdrawalView(
            withdrawal.Id,
            withdrawal.AssetId,
            withdrawal.Chain,
            withdrawal.DestinationAddress,
            withdrawal.Amount.ToString(CultureInfo.InvariantCulture),
            withdrawal.Fee.ToString(CultureInfo.InvariantCulture),
            withdrawal.Status.ToString(),
            withdrawal.TransactionHash,
            withdrawal.CreatedAt);
    }

    public async Task<(IReadOnlyList<WithdrawalAdminRow> Items, int TotalCount)> SearchAsync(
        WithdrawalAdminFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = Filtered(filter);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items.Select(ToAdminRow).ToList(), totalCount);
    }

    public async Task<WithdrawalTotals> GetTotalsAsync(
        WithdrawalAdminFilter filter, CancellationToken cancellationToken = default)
    {
        // Narrow projection over the WHOLE filtered set (unpaged), folded client-side — see
        // PaymentIntentDirectory.GetTotalsAsync for why (no SQL Sum() translation precedent for this
        // project's BigInteger money mapping, §14).
        var rows = await Filtered(filter)
            .Select(w => new { w.Amount, w.Fee, w.AssetId })
            .ToListAsync(cancellationToken);

        var totalAmount = rows.Aggregate(BigInteger.Zero, (sum, r) => sum + r.Amount);
        var totalFee = rows.Aggregate(BigInteger.Zero, (sum, r) => sum + r.Fee);
        var distinctAssetCount = rows.Select(r => r.AssetId).Distinct().Count();

        return new WithdrawalTotals(
            totalAmount.ToString(CultureInfo.InvariantCulture), totalFee.ToString(CultureInfo.InvariantCulture),
            distinctAssetCount);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(CancellationToken cancellationToken = default)
    {
        // One grouped COUNT over the domain statuses, then folded into the effective vocabulary — so
        // "pending" correctly sums Reserving+Approved+Signing+Broadcast and "failed" sums Rejected+Failed,
        // using the SAME mapping the rows and the filter use.
        var counts = await context.Withdrawals.AsNoTracking()
            .GroupBy(w => w.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var folded = WithdrawalEffectiveStatuses.All.ToDictionary(s => s, _ => 0);
        foreach (var row in counts)
            folded[EffectiveStatus(row.Status)] += row.Count;

        return folded;
    }

    private IQueryable<WithdrawalEntity> Filtered(WithdrawalAdminFilter filter)
    {
        var query = context.Withdrawals.AsNoTracking()
            .Where(w => filter.MerchantId == null || w.MerchantId == filter.MerchantId)
            .Where(w => filter.MerchantIds == null || filter.MerchantIds.Contains(w.MerchantId))
            .Where(w => filter.SystemOrderNumber == null || w.Id == filter.SystemOrderNumber)
            .Where(w => filter.MerchantOrderNumber == null || w.MerchantTransactionId == filter.MerchantOrderNumber)
            .Where(w => filter.ReceivingAddress == null || w.DestinationAddress == filter.ReceivingAddress)
            .Where(w => filter.Network == null || w.Chain == filter.Network)
            .Where(w => filter.AssetId == null || w.AssetId == filter.AssetId)
            .Where(w => filter.FromDate == null || w.CreatedAt >= filter.FromDate)
            .Where(w => filter.ToDate == null || w.CreatedAt <= filter.ToDate);

        // Optional kind filter ("User" | "Merchant"). An unrecognised value is ignored (no narrowing) — the host
        // pre-validates the query param, so this is just defensive.
        if (!string.IsNullOrWhiteSpace(filter.Kind)
            && Enum.TryParse<WithdrawalKind>(filter.Kind, ignoreCase: true, out var kind))
            query = query.Where(w => w.Kind == kind);

        // Effective-status filter. Unlike Kind above, an unrecognised value matches NOTHING rather than being
        // ignored: silently returning every withdrawal to an operator who asked for one queue would hide work
        // behind a filter they believe is applied. The host validates first, so this is the second line.
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var statuses = DomainStatusesFor(filter.Status);
            query = query.Where(w => statuses.Contains(w.Status));
        }

        return query;
    }

    /// <summary>
    /// The inverse of <see cref="EffectiveStatus"/> — deliberately kept adjacent to it, because the two
    /// drifting apart would mean a filter that silently excludes rows the same screen displays under that
    /// exact label. Every domain status appears in exactly one bucket here.
    /// </summary>
    private static WithdrawalStatus[] DomainStatusesFor(string effectiveStatus) =>
        effectiveStatus.Trim().ToLowerInvariant() switch
        {
            "confirmed" => [WithdrawalStatus.Confirmed],
            "failed" => [WithdrawalStatus.Rejected, WithdrawalStatus.Failed],
            "pending_approval" => [WithdrawalStatus.PendingApproval],
            "pending_merchant_approval" => [WithdrawalStatus.PendingMerchantApproval],
            "insufficient_balance" => [WithdrawalStatus.AwaitingFunds],
            "awaiting_release" => [WithdrawalStatus.AwaitingRelease],
            "pending_admin_audit" => [WithdrawalStatus.PendingAdminAudit],
            "pending_finance_transfer" => [WithdrawalStatus.PendingFinanceTransfer],
            "finance_settled" => [WithdrawalStatus.FinanceSettled],
            "pending" =>
            [
                WithdrawalStatus.Reserving, WithdrawalStatus.Approved,
                WithdrawalStatus.Signing, WithdrawalStatus.Broadcast,
            ],
            _ => [],
        };

    private static WithdrawalAdminRow ToAdminRow(WithdrawalEntity withdrawal) => new(
        withdrawal.MerchantId,
        withdrawal.Id,
        withdrawal.MerchantTransactionId,
        withdrawal.Chain,
        withdrawal.AssetId,
        withdrawal.DestinationAddress,
        withdrawal.Amount.ToString(CultureInfo.InvariantCulture),
        withdrawal.Fee.ToString(CultureInfo.InvariantCulture),
        EffectiveStatus(withdrawal.Status),
        withdrawal.StatusReason,
        withdrawal.Confirmations,
        withdrawal.TransactionHash,
        withdrawal.SourceWalletId,
        withdrawal.CreatedAt,
        withdrawal.Kind.ToString(),
        withdrawal.AuditedBy,
        withdrawal.AuditedAt,
        withdrawal.CompletedBy,
        withdrawal.CompletedAt,
        withdrawal.SettlementSourceAddress);

    /// <summary>"pending" | "pending_merchant_approval" | "pending_approval" | "insufficient_balance" | "awaiting_release" | "confirmed" |
    /// "failed" — withdrawals have no "expired" state. The states that need a human, not the worker, to move
    /// forward are each surfaced distinctly: <c>PendingMerchantApproval</c> (the merchant's own sign-off, done in
    /// their portal — platform staff see it but do not action it), <c>PendingApproval</c> (platform approve/reject),
    /// <c>AwaitingFunds</c> (reload the hot wallet, then it self-resumes), <c>AwaitingRelease</c> (operator release) — see
    /// <c>OpsWithdrawalApprovalEndpoints</c>/<c>OpsWithdrawalFundingEndpoints</c>. The rest of the pre-confirm
    /// pipeline (Reserving/Approved/Signing/Broadcast) collapses to "pending".</summary>
    private static string EffectiveStatus(WithdrawalStatus status) => status switch
    {
        WithdrawalStatus.Confirmed => "confirmed",
        WithdrawalStatus.Rejected or WithdrawalStatus.Failed => "failed",
        WithdrawalStatus.PendingApproval => "pending_approval",
        WithdrawalStatus.PendingMerchantApproval => "pending_merchant_approval",
        WithdrawalStatus.AwaitingFunds => "insufficient_balance",
        WithdrawalStatus.AwaitingRelease => "awaiting_release",
        WithdrawalStatus.PendingAdminAudit => "pending_admin_audit",
        WithdrawalStatus.PendingFinanceTransfer => "pending_finance_transfer",
        WithdrawalStatus.FinanceSettled => "finance_settled",
        _ => "pending",
    };
}
