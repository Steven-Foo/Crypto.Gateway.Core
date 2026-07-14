using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;

/// <summary>
/// A deterministic, in-memory implementation of the read-only chain capabilities, for Development and
/// tests. It is the same DI seam a real JSON-RPC adapter plugs into — swapping the provider is a
/// configuration change, not a code change (§8). Its extra driving methods (<see cref="AddBlock"/>,
/// <see cref="ReplaceBlock"/>, <see cref="SetFinalizedHeight"/>) let a test script a chain, including a
/// reorg, without a node. Thread-safe: a background scanner and a test may touch it concurrently.
/// </summary>
public sealed class InMemoryChainSource : IDepositScanner, IChainStatusReader
{
    private readonly object _gate = new();
    private readonly Dictionary<Chain, ChainState> _chains = [];

    // ── Driving API (dev/test only) ──────────────────────────────────────────────

    /// <summary>Appends (or overwrites) a block at <paramref name="blockNumber"/> with its transfers.</summary>
    public void AddBlock(Chain chain, long blockNumber, string blockHash, params DetectedTransfer[] transfers)
    {
        lock (_gate)
        {
            var state = GetOrAdd(chain);
            state.Blocks[blockNumber] = new BlockData(blockHash, [.. transfers]);
        }
    }

    /// <summary>Reorg: replace the block at a height with a different hash/contents, dropping anything above it.</summary>
    public void ReplaceBlock(Chain chain, long blockNumber, string newBlockHash, params DetectedTransfer[] transfers)
    {
        lock (_gate)
        {
            var state = GetOrAdd(chain);
            foreach (var higher in state.Blocks.Keys.Where(h => h > blockNumber).ToList())
                state.Blocks.Remove(higher);
            state.Blocks[blockNumber] = new BlockData(newBlockHash, [.. transfers]);
        }
    }

    public void SetFinalizedHeight(Chain chain, long height)
    {
        lock (_gate)
        {
            GetOrAdd(chain).FinalizedHeight = height;
        }
    }

    // ── IDepositScanner ──────────────────────────────────────────────────────────

    public Task<IReadOnlyList<DetectedTransfer>> ScanAsync(
        Chain chain, long fromBlock, long toBlock, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_chains.TryGetValue(chain, out var state))
                return Task.FromResult<IReadOnlyList<DetectedTransfer>>([]);

            var transfers = state.Blocks
                .Where(b => b.Key >= fromBlock && b.Key <= toBlock)
                .SelectMany(b => b.Value.Transfers)
                .ToList();

            return Task.FromResult<IReadOnlyList<DetectedTransfer>>(transfers);
        }
    }

    // ── IChainStatusReader ───────────────────────────────────────────────────────

    public Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var tip = _chains.TryGetValue(chain, out var state) && state.Blocks.Count > 0
                ? state.Blocks.Keys.Max()
                : 0L;
            return Task.FromResult(tip);
        }
    }

    public Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_chains.TryGetValue(chain, out var state) && state.Blocks.TryGetValue(blockNumber, out var block))
                return Task.FromResult<BlockRef?>(new BlockRef(blockNumber, block.Hash));

            return Task.FromResult<BlockRef?>(null);
        }
    }

    public Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var finalized = _chains.TryGetValue(chain, out var state) ? state.FinalizedHeight : 0L;
            return Task.FromResult(finalized);
        }
    }

    private ChainState GetOrAdd(Chain chain)
    {
        if (!_chains.TryGetValue(chain, out var state))
            _chains[chain] = state = new ChainState();
        return state;
    }

    private sealed class ChainState
    {
        public long FinalizedHeight { get; set; }
        public SortedDictionary<long, BlockData> Blocks { get; } = [];
    }

    private sealed record BlockData(string Hash, List<DetectedTransfer> Transfers);
}
