using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class HotWalletTopUpRepository(WithdrawalDbContext context) : IHotWalletTopUpRepository
{
    public async Task<TopUpRecordOutcome> AddIfNewAsync(HotWalletTopUp topUp, CancellationToken cancellationToken = default)
    {
        context.HotWalletTopUps.Add(topUp);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return TopUpRecordOutcome.Recorded;
        }
        catch (DbUpdateException ex) when (IsDuplicateHash(ex))
        {
            // Detach so the context stays usable — the caller turns this into a business failure, not a crash.
            context.Entry(topUp).State = EntityState.Detached;
            return TopUpRecordOutcome.Duplicate;
        }
    }

    public Task<HotWalletTopUp?> FindByTransactionHashAsync(string transactionHash, CancellationToken cancellationToken = default) =>
        context.HotWalletTopUps
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TransactionHash == transactionHash, cancellationToken);

    public async Task<(IReadOnlyList<HotWalletTopUp> Items, int TotalCount)> SearchAsync(
        Chain? chain, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = context.HotWalletTopUps.AsNoTracking()
            .Where(t => chain == null || t.Chain == chain);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(t => t.RecordedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Translates the unique-index violation into a business outcome, keeping the EF/SQL specifics inside
    /// Infrastructure (§4.4). Named explicitly rather than catching any DbUpdateException, so an unrelated
    /// failure still surfaces as one.
    /// </summary>
    private static bool IsDuplicateHash(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UX_HotWalletTopUp_TxHash", StringComparison.OrdinalIgnoreCase) == true;
}
