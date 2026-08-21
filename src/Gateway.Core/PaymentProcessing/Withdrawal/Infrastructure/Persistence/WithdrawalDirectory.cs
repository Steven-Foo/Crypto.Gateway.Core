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

        return query;
    }

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
        withdrawal.Kind.ToString());

    /// <summary>"pending" | "pending_approval" | "insufficient_balance" | "awaiting_release" | "confirmed" |
    /// "failed" — withdrawals have no "expired" state. The states that need a human, not the worker, to move
    /// forward are each surfaced distinctly: <c>PendingApproval</c> (approve/reject), <c>AwaitingFunds</c>
    /// (reload the hot wallet, then it self-resumes), <c>AwaitingRelease</c> (operator release) — see
    /// <c>OpsWithdrawalApprovalEndpoints</c>/<c>OpsWithdrawalFundingEndpoints</c>. The rest of the pre-confirm
    /// pipeline (Reserving/Approved/Signing/Broadcast) collapses to "pending".</summary>
    private static string EffectiveStatus(WithdrawalStatus status) => status switch
    {
        WithdrawalStatus.Confirmed => "confirmed",
        WithdrawalStatus.Rejected or WithdrawalStatus.Failed => "failed",
        WithdrawalStatus.PendingApproval => "pending_approval",
        WithdrawalStatus.AwaitingFunds => "insufficient_balance",
        WithdrawalStatus.AwaitingRelease => "awaiting_release",
        _ => "pending",
    };
}
