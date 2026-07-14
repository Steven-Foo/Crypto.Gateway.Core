using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// Fans the multi-chain capability ports out to the per-chain <see cref="IChainAdapter"/>s. Registered
/// as the real-provider replacement for <c>InMemoryChainSource</c>: swapping dev↔prod is a DI choice,
/// the consuming modules (Deposit, …) never change. An unconfigured chain fails fast.
/// </summary>
public sealed class RoutingChainSource : IDepositScanner, IChainStatusReader
{
    private readonly IReadOnlyDictionary<Chain, IChainAdapter> _adapters;

    public RoutingChainSource(IEnumerable<IChainAdapter> adapters) =>
        _adapters = adapters.ToDictionary(a => a.Chain);

    public Task<IReadOnlyList<DetectedTransfer>> ScanAsync(Chain chain, long fromBlock, long toBlock, CancellationToken cancellationToken = default) =>
        For(chain).ScanAsync(chain, fromBlock, toBlock, cancellationToken);

    public Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default) =>
        For(chain).GetTipHeightAsync(chain, cancellationToken);

    public Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default) =>
        For(chain).GetBlockAsync(chain, blockNumber, cancellationToken);

    public Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default) =>
        For(chain).GetFinalizedHeightAsync(chain, cancellationToken);

    private IChainAdapter For(Chain chain) =>
        _adapters.TryGetValue(chain, out var adapter)
            ? adapter
            : throw new NotSupportedException($"No blockchain adapter is registered for {chain}.");
}
