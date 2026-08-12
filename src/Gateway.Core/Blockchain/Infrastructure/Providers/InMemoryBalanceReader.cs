using System.Collections.Concurrent;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// A deterministic, in-memory <see cref="IBalanceReader"/> for Development and tests — the same DI seam the
/// real TRON <c>eth_call balanceOf</c> adapter plugs into (§8). A test pins an address's balance via
/// <see cref="Set"/>; an unset address reads as zero (never held ⇒ nothing on chain). Thread-safe: the
/// reconciliation worker and a test may touch it concurrently.
/// </summary>
public sealed class InMemoryBalanceReader : IBalanceReader
{
    private readonly ConcurrentDictionary<string, BigInteger> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public void Set(Chain chain, string address, Guid assetId, BigInteger balance) =>
        _byKey[Key(chain, address, assetId)] = balance;

    public Task<BigInteger> GetBalanceAsync(Chain chain, string address, Guid assetId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byKey.GetValueOrDefault(Key(chain, address, assetId), BigInteger.Zero));

    private static string Key(Chain chain, string address, Guid assetId) => $"{chain}:{address}:{assetId}";
}
