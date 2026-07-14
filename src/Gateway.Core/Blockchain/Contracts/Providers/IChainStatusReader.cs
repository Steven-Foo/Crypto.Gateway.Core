using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>A block's height and its hash — the pair a reorg check compares against.</summary>
public sealed record BlockRef(long BlockNumber, string BlockHash);

/// <summary>
/// Read-only chain status (§8), used to confirm deposits: how tall the chain is, what the canonical
/// block at a height is (so a stored deposit whose block hash no longer matches was reorged out), and
/// how far finality has advanced (for finality-based crediting, e.g. Solana 'finalized').
/// </summary>
public interface IChainStatusReader
{
    /// <summary>Current best-block height of the chain.</summary>
    Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default);

    /// <summary>
    /// The canonical block at <paramref name="blockNumber"/>, or null if the chain is shorter. A hash
    /// that differs from a deposit's stored <c>BlockHash</c> means that deposit was orphaned by a reorg.
    /// </summary>
    Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Highest block considered final/irreversible. For confirmation-count chains this trails the tip
    /// by the required depth; for commitment chains (Solana) it reflects the 'finalized' commitment.
    /// </summary>
    Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default);
}
