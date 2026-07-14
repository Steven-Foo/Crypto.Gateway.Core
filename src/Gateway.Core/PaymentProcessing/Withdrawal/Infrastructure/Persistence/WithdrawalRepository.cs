using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WithdrawalEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain.Withdrawal;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Infrastructure.Persistence;

public sealed class WithdrawalRepository(WithdrawalDbContext context) : IWithdrawalRepository
{
    public Task<WithdrawalEntity?> FindByIdempotencyKeyAsync(Guid merchantId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        context.Withdrawals.SingleOrDefaultAsync(w => w.MerchantId == merchantId && w.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<WithdrawalEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.Withdrawals.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);

    public async Task<IReadOnlyList<WithdrawalEntity>> GetByStatusesAsync(
        IReadOnlyCollection<WithdrawalStatus> statuses, CancellationToken cancellationToken = default) =>
        await context.Withdrawals.Where(w => statuses.Contains(w.Status)).ToListAsync(cancellationToken);

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

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    private static bool IsIdempotencyViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_Withdrawal_Idempotency", StringComparison.Ordinal);
}
