using Medallion.Threading;
using Medallion.Threading.Redis;
using StackExchange.Redis;

namespace CryptoPaymentEngine.Infrastructure.Locking;

/// <summary>
/// Redis-backed <see cref="IDistributedLockFactory"/> (Medallion DistributedLock.Redis over
/// StackExchange.Redis). One instance per process; the underlying multiplexer is thread-safe and shared.
/// </summary>
public sealed class RedisDistributedLockFactory : IDistributedLockFactory
{
    private readonly RedisDistributedSynchronizationProvider _provider;

    public RedisDistributedLockFactory(IConnectionMultiplexer redis) =>
        _provider = new RedisDistributedSynchronizationProvider(redis.GetDatabase());

    public async Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IDistributedLock @lock = _provider.CreateLock(key);
        return await @lock.AcquireAsync(timeout, cancellationToken);
    }
}
