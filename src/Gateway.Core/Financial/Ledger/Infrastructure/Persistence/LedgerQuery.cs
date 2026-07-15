using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

/// <summary>
/// Reads a merchant's available balance straight from the <c>AccountBalance</c> cache — the running
/// projection maintained in the same transaction as every posting, so it is always consistent with the
/// journal it is derived from. Read-only and untracked: it opens no account and posts nothing.
/// </summary>
public sealed class LedgerQuery(LedgerDbContext context) : ILedgerQuery
{
    public async Task<BigInteger> GetMerchantBalanceAsync(
        Guid merchantId, Guid assetId, CancellationToken cancellationToken = default)
    {
        // MerchantLiability(merchant, asset) is credit-normal; its cached balance is what we owe the
        // merchant right now. No account/balance row yet ⇒ the merchant has never transacted this asset
        // ⇒ zero (FirstOrDefaultAsync yields default(BigInteger), which is 0).
        return await (
            from account in context.Accounts.AsNoTracking()
            where account.AccountType == AccountType.MerchantLiability
               && account.OwnerType == OwnerType.Merchant
               && account.OwnerId == merchantId
               && account.AssetId == assetId
            join balance in context.AccountBalances.AsNoTracking() on account.Id equals balance.Id
            select balance.Balance)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
