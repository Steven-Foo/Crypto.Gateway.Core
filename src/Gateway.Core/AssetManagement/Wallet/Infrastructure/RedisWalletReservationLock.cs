using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using StackExchange.Redis;

namespace CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Infrastructure;

/// <summary>
/// <see cref="IWalletReservationLock"/> over the shared Redis connection (StackExchange.Redis directly —
/// this is a keyed reservation with a TTL, not a mutual-exclusion lock, so it does not go through
/// <c>IDistributedLockFactory</c>/Medallion). <c>SET key value NX</c> is the atomic non-blocking claim;
/// deleting an absent key is a no-op, so releasing an unheld or already-expired reservation is harmless.
/// </summary>
public sealed class RedisWalletReservationLock(IConnectionMultiplexer redis) : IWalletReservationLock
{
    private readonly IDatabase _db = redis.GetDatabase();

    public Task<bool> TryReserveAsync(Guid walletId, string referenceId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        _db.StringSetAsync(KeyFor(walletId), referenceId, ttl, When.NotExists);

    public Task ReleaseAsync(Guid walletId, CancellationToken cancellationToken = default) =>
        _db.KeyDeleteAsync(KeyFor(walletId));

    private static string KeyFor(Guid walletId) => $"wallet:reservation:{walletId}";
}
