namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// The exact TRON node operations the adapter needs, so the adapter's mapping logic can be tested
/// against a fake without a live node. Read-only: it never sends a key or signs (§10). Implemented by
/// <c>TronRpc</c> over TronGrid/full-node HTTP.
/// </summary>
public interface ITronRpc
{
    /// <summary><c>eth_blockNumber</c> — current best block height.</summary>
    Task<long> GetBlockNumberAsync(CancellationToken cancellationToken = default);

    /// <summary><c>eth_getBlockByNumber</c> — header (number + hash) at a height, or null if beyond the tip.</summary>
    Task<TronBlockDto?> GetBlockByNumberAsync(long blockNumber, CancellationToken cancellationToken = default);

    /// <summary><c>eth_getLogs</c> for TRC-20 Transfer events across the given token contracts (0x-hex) in a range.</summary>
    Task<IReadOnlyList<TronLogDto>> GetTransferLogsAsync(
        long fromBlock, long toBlock, IReadOnlyCollection<string> contractHexAddresses, CancellationToken cancellationToken = default);

    /// <summary><c>/walletsolidity/getnowblock</c> — the latest solidified (irreversible) block number.</summary>
    Task<long> GetSolidifiedBlockNumberAsync(CancellationToken cancellationToken = default);
}
