namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;

/// <summary>
/// A wallet's business-level reservation for a deposit invoice — distinct from
/// <c>IDistributedLockFactory</c> (which is a brief mutual-exclusion guard around one DB operation). This is
/// a long-lived (tens of minutes), non-blocking claim: try one wallet, if it's taken move straight to the
/// next candidate rather than waiting. Released explicitly when the invoice resolves (matched/failed), or
/// automatically once <paramref name="ttl"/> elapses (covers the never-arrived/expired case with no extra
/// worker needed — the reservation and the invoice's Waiting window end together).
///
/// This is a performance/concentration optimisation, not the money-safety boundary: the PaymentIntent
/// table's own filtered UNIQUE index on <c>(WalletId) WHERE Status = 'Waiting'</c> remains the backstop, so
/// losing a reservation (Redis down, key evicted) degrades to "mint an extra wallet," never to two invoices
/// double-assigned to the same address.
/// </summary>
public interface IWalletReservationLock
{
    /// <summary>
    /// Attempts to claim <paramref name="walletId"/> for <paramref name="referenceId"/>. Non-blocking:
    /// returns <see langword="false"/> immediately if another reservation already holds it, rather than
    /// waiting. On success the claim expires automatically after <paramref name="ttl"/> unless released first.
    /// </summary>
    Task<bool> TryReserveAsync(Guid walletId, string referenceId, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Releases the reservation early (invoice matched or manually failed). A no-op if not held.</summary>
    Task ReleaseAsync(Guid walletId, CancellationToken cancellationToken = default);
}
