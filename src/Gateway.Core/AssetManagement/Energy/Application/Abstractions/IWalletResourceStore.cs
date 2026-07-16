namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;

/// <summary>
/// The latest observed resource state per wallet — a derived snapshot (MongoDB), never money truth (§2).
/// Upsert-by-wallet: one current document per wallet, overwritten each poll.
/// </summary>
public interface IWalletResourceStore
{
    Task UpsertAsync(WalletResourceSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<WalletResourceSnapshot?> GetAsync(Guid walletId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Append-only time series of resource observations (MongoDB), the raw material Phase 5c forecasting
/// will consume. Never money truth (§2).
/// </summary>
public interface IResourceHistoryStore
{
    Task AppendAsync(WalletResourceSnapshot snapshot, CancellationToken cancellationToken = default);
}
