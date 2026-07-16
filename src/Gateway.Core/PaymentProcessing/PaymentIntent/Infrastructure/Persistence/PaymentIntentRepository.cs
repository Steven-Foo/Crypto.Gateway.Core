using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentIntentEntity = CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain.PaymentIntent;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Infrastructure.Persistence;

public sealed class PaymentIntentRepository(PaymentIntentDbContext context, IWalletDirectory walletDirectory) : IPaymentIntentRepository
{
    public Task<PaymentIntentEntity?> FindByMerchantReferenceAsync(
        Guid merchantId, string merchantTransactionId, CancellationToken cancellationToken = default) =>
        context.PaymentIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.MerchantId == merchantId && i.MerchantTransactionId == merchantTransactionId, cancellationToken);

    public async Task<ReusableAddress?> FindReusableAddressAsync(
        Guid merchantId, Chain chain, CancellationToken cancellationToken = default)
    {
        // Candidates come from the Wallet module (Contracts-only, §4.5) rather than this module's own
        // invoice history, so a freshly pre-provisioned pool (e.g. the 10 wallets minted at merchant
        // onboarding) is visible here even before any of them has ever had an invoice created against it.
        // Ordered by deposit activity descending — the non-money-duplicating proxy for "closest to a sweep
        // threshold" (see Wallet.DepositsReceivedCount).
        var candidates = await walletDirectory.ListAssignedWalletsAsync(merchantId, chain, cancellationToken);
        if (candidates.Count == 0)
            return null;

        // An address is free once no invoice is Waiting on it (the expiry sweep flips lapsed ones out).
        var busy = await context.PaymentIntents.AsNoTracking()
            .Where(i => i.Status == PaymentIntentStatus.Waiting)
            .Select(i => i.WalletId)
            .ToListAsync(cancellationToken);
        var busySet = busy.Count == 0 ? [] : busy.ToHashSet();

        var free = candidates.FirstOrDefault(w => !busySet.Contains(w.WalletId));
        return free is null ? null : new ReusableAddress(free.WalletId, free.Address);
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
