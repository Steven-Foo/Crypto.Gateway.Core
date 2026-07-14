namespace CryptoPaymentEngine.Infrastructure.Locking;

/// <summary>
/// A single, transport-agnostic seam for distributed locks, reused by every module (§7.4). Modules
/// depend on this, not on the Redis client or Medallion directly, so the lock backend can change —
/// and tests can substitute a no-op — without touching business code.
///
/// The lock is a <em>performance</em> guard (single-flight to reduce contention), never the sole
/// correctness guard: the money-critical invariant is still enforced by the database (a rowversion on
/// the balance row, a UNIQUE on the journal). Losing the lock must never lose money.
/// </summary>
public interface IDistributedLockFactory
{
    /// <summary>
    /// Acquires the lock named <paramref name="key"/>, waiting up to <paramref name="timeout"/>.
    /// Dispose the returned handle to release. Throws if the lock cannot be acquired within the timeout.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default);
}
