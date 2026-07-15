using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

public sealed class PaymentIntentRepository(PaymentIntentDbContext context) : IPaymentIntentRepository
{
    public Task<PaymentIntentEntity?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.MerchantId == merchantId && i.MerchantTransactionId == merchantTransactionId, cancellationToken);

    public async Task<ReusableAddress?> FindReusableAddressAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default)
    {
        // An address is free once no invoice is Waiting on it (the expiry sweep flips lapsed ones out).
        var busy = context.PaymentIntents
            .Where(i => i.Status == PaymentIntentStatus.Waiting)
            .Select(i => i.WalletId);

        return await context.PaymentIntents.AsNoTracking()
            .Where(i => i.MerchantId == merchantId && i.Chain == chain && !busy.Contains(i.WalletId))
            .OrderBy(i => i.CreatedAt)
            .Select(i => new ReusableAddress(i.WalletId, i.Address))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<PaymentIntentEntity?> FindWaitingByWalletAsync(Guid walletId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents
            .SingleOrDefaultAsync(i => i.WalletId == walletId && i.Status == PaymentIntentStatus.Waiting, cancellationToken);

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
        var stale = await context.PaymentIntents
            .Where(i => i.Status == PaymentIntentStatus.Waiting && i.ExpiresAt <= now)
            .OrderBy(i => i.ExpiresAt)
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
