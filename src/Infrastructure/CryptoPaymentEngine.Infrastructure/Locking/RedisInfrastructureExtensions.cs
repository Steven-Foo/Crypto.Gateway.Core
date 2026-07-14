using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace CryptoPaymentEngine.Infrastructure.Locking;

public static class RedisInfrastructureExtensions
{
    /// <summary>
    /// Registers the shared Redis connection and the distributed-lock factory once for the whole host.
    /// Every module that needs a lock (or, later, a cache) reuses these — no per-module multiplexer.
    /// The multiplexer is a singleton (it is designed to be shared and is expensive to create).
    /// </summary>
    public static IServiceCollection AddRedisInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            // Don't abort host startup if Redis is momentarily unreachable; reconnect in the background.
            // Correctness never depends on the lock (rowversion/UNIQUE do) — the lock is an optimisation.
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        services.TryAddSingleton<IDistributedLockFactory, RedisDistributedLockFactory>();
        return services;
    }
}
