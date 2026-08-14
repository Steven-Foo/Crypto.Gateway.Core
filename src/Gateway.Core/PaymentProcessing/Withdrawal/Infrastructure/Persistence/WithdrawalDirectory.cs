using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using Microsoft.EntityFrameworkCore;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalDirectory(WithdrawalDbContext context) : IWithdrawalDirectory
{
    public async Task<WithdrawalView?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default)
    {
        var withdrawal = await context.Withdrawals.AsNoTracking()
            .SingleOrDefaultAsync(
                w => w.MerchantId == merchantId && w.MerchantTransactionId == merchantTransactionId, cancellationToken);

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
        var query = context.Withdrawals.AsNoTracking()
            .Where(w => filter.MerchantId == null || w.MerchantId == filter.MerchantId)
            .Where(w => filter.SystemOrderNumber == null || w.Id == filter.SystemOrderNumber)
            .Where(w => filter.MerchantOrderNumber == null || w.MerchantTransactionId == filter.MerchantOrderNumber)
            .Where(w => filter.ReceivingAddress == null || w.DestinationAddress == filter.ReceivingAddress)
            .Where(w => filter.Network == null || w.Chain == filter.Network)
            .Where(w => filter.AssetId == null || w.AssetId == filter.AssetId)
            .Where(w => filter.FromDate == null || w.CreatedAt >= filter.FromDate)
            .Where(w => filter.ToDate == null || w.CreatedAt <= filter.ToDate);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(w => w.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items.Select(ToAdminRow).ToList(), totalCount);
    }

    private static WithdrawalAdminRow ToAdminRow(WithdrawalEntity withdrawal) => new(
        withdrawal.MerchantId,
        withdrawal.Id,
        withdrawal.MerchantTransactionId,
        withdrawal.Chain,
        withdrawal.AssetId,
        withdrawal.DestinationAddress,
        withdrawal.Amount.ToString(CultureInfo.InvariantCulture),
        EffectiveStatus(withdrawal.Status),
        withdrawal.StatusReason,
        withdrawal.Confirmations,
        withdrawal.CreatedAt);

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
