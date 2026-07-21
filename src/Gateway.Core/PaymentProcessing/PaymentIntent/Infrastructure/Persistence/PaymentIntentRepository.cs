using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

/// <summary>
/// Wallet candidate selection (who's free to reuse) now lives in the Application layer, walking
/// <c>IWalletDirectory</c> and reserving via <c>IWalletReservationLock</c> — this repository is persistence
/// only: idempotency lookups, the insert whose unique indexes are the money-safety backstop, and expiry.
/// </summary>
public sealed class PaymentIntentRepository(PaymentIntentDbContext context) : IPaymentIntentRepository
{
    public Task<PaymentIntentEntity?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.MerchantId == merchantId && i.MerchantTransactionId == merchantTransactionId, cancellationToken);

    public Task<PaymentIntentEntity?> FindWaitingByWalletAsync(Guid walletId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents
            .SingleOrDefaultAsync(i => i.WalletId == walletId && i.Status == PaymentIntentStatus.Waiting, cancellationToken);

    public Task<PaymentIntentEntity?> FindByPublicReferenceAsync(Guid publicReference, CancellationToken cancellationToken = default) =>
        context.PaymentIntents
            .SingleOrDefaultAsync(i => i.PublicReference == publicReference, cancellationToken);

    public Task<bool> IsDepositMatchedAsync(Guid depositId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents.AnyAsync(i => i.MatchedDepositId == depositId, cancellationToken);

    public async Task<PaymentIntentAddOutcome> TryAddAsync(PaymentIntentEntity intent, CancellationToken cancellationToken = default)
    {
        context.PaymentIntents.Add(intent);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return PaymentIntentAddOutcome.Added;
        }
        catch (DbUpdateException ex) when (IsUnique(ex, "UX_PaymentIntent_Idempotency"))
        {
            context.Entry(intent).State = EntityState.Detached;
            return PaymentIntentAddOutcome.DuplicateReference;
        }
        catch (DbUpdateException ex) when (IsUnique(ex, "UX_PaymentIntent_LiveWallet"))
        {
            context.Entry(intent).State = EntityState.Detached;
            return PaymentIntentAddOutcome.AddressBusy;
        }
    }

    public async Task<int> ExpireStaleAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
    {
        // GraceExpiresAt, not ExpiresAt: the payer is already shown "expired" past ExpiresAt (see
        // PaymentIntentDirectory), but the wallet stays reserved and matchable through the grace window —
        // only once the hard cutoff passes does the address actually free up for reuse.
        var stale = await context.PaymentIntents
            .Where(i => i.Status == PaymentIntentStatus.Waiting && i.GraceExpiresAt <= now)
            .OrderBy(i => i.GraceExpiresAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var intent in stale)
            intent.Expire(now);

        if (stale.Count > 0)
            await context.SaveChangesAsync(cancellationToken);

        return stale.Count;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    private static bool IsUnique(DbUpdateException ex, string indexName) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains(indexName, StringComparison.Ordinal);
}
