using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Domain;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application.Abstractions;

/// <summary>
/// Resolves the account for a <c>(type, owner, asset)</c> tuple, creating it on first use. Account
/// creation is idempotent under concurrency: the DB's UNIQUE <c>(OwnerType, OwnerId, AssetId,
/// AccountType)</c> is the arbiter, so a race resolves to one row, never two (§7.3). Implemented in
/// Infrastructure; the Application never touches EF.
/// </summary>
public interface ILedgerAccountStore
{
    Task<Account> GetOrCreateAsync(
        AccountType accountType,
        OwnerType ownerType,
        Guid? ownerId,
        Guid assetId,
        CancellationToken cancellationToken = default);
}
