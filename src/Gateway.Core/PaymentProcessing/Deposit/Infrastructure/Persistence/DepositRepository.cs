using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using DepositEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Domain.Deposit;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Infrastructure.Persistence;

public sealed class DepositRepository(DepositDbContext context) : IDepositRepository
{
    public async Task<DepositRecordOutcome> AddIfNewAsync(DepositEntity deposit, CancellationToken cancellationToken = default)
    {
        context.Deposits.Add(deposit);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return DepositRecordOutcome.Recorded;
        }
        catch (DbUpdateException ex) when (IsDedupViolation(ex))
        {
            // Already recorded on a prior scan — the UNIQUE index settled the race, not app logic.
            context.Entry(deposit).State = EntityState.Detached;
            return DepositRecordOutcome.Duplicate;
        }
    }

    /// <summary>
    /// The deposits still worth watching on this chain: not yet credited, or credited but not yet buried
    /// beyond reorg reach. Finalized deposits are excluded — they can never change, so re-reading their
    /// block would burn one RPC per pass forever (see <c>Deposit.MarkFinalized</c>).
    /// </summary>
    public async Task<IReadOnlyList<DepositEntity>> GetTrackableAsync(Chain chain, CancellationToken cancellationToken = default) =>
        await context.Deposits
            .Where(d => d.Chain == chain
                        && d.FinalizedAt == null
                        && (d.Status == DepositStatus.Detected || d.Status == DepositStatus.Confirmed))
            .ToListAsync(cancellationToken);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    private static bool IsDedupViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("UX_Deposit_Tx", StringComparison.Ordinal);
}
