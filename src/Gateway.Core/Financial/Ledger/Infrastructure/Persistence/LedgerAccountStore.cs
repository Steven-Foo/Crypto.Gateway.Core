using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Infrastructure.Persistence;

/// <summary>
/// Resolves-or-creates ledger accounts. The DB's <c>UX_Account_Natural</c> unique index is the race
/// arbiter: if two callers create the same account at once, the loser catches the unique violation
/// and re-reads the winner's row (§7.3).
/// </summary>
public sealed class LedgerAccountStore(LedgerDbContext context, TimeProvider timeProvider) : ILedgerAccountStore
{
    public async Task<Account> GetOrCreateAsync(
        AccountType accountType,
        OwnerType ownerType,
        Guid? ownerId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(accountType, ownerType, ownerId, assetId, cancellationToken);
        if (existing is not null)
            return existing;

        var opened = Account.Open(accountType, ownerType, ownerId, assetId, timeProvider.GetUtcNow());
        if (opened.IsFailure)
            throw new DomainException($"Cannot open ledger account: {opened.Error!.Code} — {opened.Error!.Message}");

        var account = opened.Value;
        context.Accounts.Add(account);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return account;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the create race — detach our doomed insert and return the row that won.
            context.Entry(account).State = EntityState.Detached;
            return await FindAsync(accountType, ownerType, ownerId, assetId, cancellationToken)
                ?? throw new DomainException("Account unique violation with no surviving row — impossible state.", ex);
        }
    }

    private Task<Account?> FindAsync(
        AccountType accountType, OwnerType ownerType, Guid? ownerId, Guid assetId, CancellationToken cancellationToken) =>
        context.Accounts.SingleOrDefaultAsync(
            a => a.AccountType == accountType && a.OwnerType == ownerType && a.OwnerId == ownerId && a.AssetId == assetId,
            cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
