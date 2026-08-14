using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalRepository(WithdrawalDbContext context) : IWithdrawalRepository
{
    public Task<WithdrawalEntity?> FindByMerchantTransactionIdAsync(Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default) =>
        context.Withdrawals.SingleOrDefaultAsync(w => w.MerchantId == merchantId && w.MerchantTransactionId == merchantTransactionId, cancellationToken);

    public Task<WithdrawalEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Withdrawals.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WithdrawalEntity>> GetByStatusesAsync(
        IReadOnlyCollection<WithdrawalStatus> statuses, CancellationToken cancellationToken = default) =>
        await context.Withdrawals.Where(w => statuses.Contains(w.Status)).ToListAsync(cancellationToken);

    private static readonly WithdrawalStatus[] InFlightStatuses = [WithdrawalStatus.Signing, WithdrawalStatus.Broadcast];

    public async Task<IReadOnlyCollection<Guid>> GetInFlightSourceWalletIdsAsync(
        Chain chain, CancellationToken cancellationToken = default)
    {
        var ids = await context.Withdrawals
            .Where(w => w.Chain == chain && w.SourceWalletId != null && InFlightStatuses.Contains(w.Status))
            .Select(w => w.SourceWalletId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        return ids;
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetWalletLastUsedAsync(
        IReadOnlyCollection<Guid> walletIds, CancellationToken cancellationToken = default)
    {
        if (walletIds.Count == 0)
            return new Dictionary<Guid, DateTimeOffset>();

        var rows = await context.Withdrawals
            .Where(w => w.SourceWalletId != null && walletIds.Contains(w.SourceWalletId!.Value))
            .GroupBy(w => w.SourceWalletId!.Value)
            .Select(g => new { WalletId = g.Key, LastUsed = g.Max(w => w.CreatedAt) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.WalletId, r => r.LastUsed);
    }

    public async Task<WithdrawalRecordOutcome> AddIfNewAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default)
    {
        context.Withdrawals.Add(withdrawal);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return WithdrawalRecordOutcome.Recorded;
        }
        catch (DbUpdateException ex) when (IsIdempotencyViolation(ex))
        {
            context.Entry(withdrawal).State = EntityState.Detached;
            return WithdrawalRecordOutcome.Duplicate;
        }
    }

    public async Task<bool> TrySaveSignedAsync(WithdrawalEntity withdrawal, CancellationToken cancellationToken = default)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker advanced this withdrawal (rowversion) — detach and leave it for the next pass.
            context.Entry(withdrawal).State = EntityState.Detached;
            return false;
        }
        catch (DbUpdateException ex) when (IsSourceWalletConflict(ex))
        {
            // Another withdrawal leased this hot wallet concurrently. Detach so the context stays usable; this
            // withdrawal is still Approved in the DB and re-allocates a different wallet next pass.
            context.Entry(withdrawal).State = EntityState.Detached;
            return false;
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    private static bool IsIdempotencyViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_Withdrawal_Idempotency", StringComparison.Ordinal);

    private static bool IsSourceWalletConflict(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_Withdrawal_InFlight_SourceWallet", StringComparison.Ordinal);
}
