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

    /// <summary>
    /// <c>/wallet/getblockbylimitnext</c> — full blocks (with native transactions) across
    /// <c>[fromBlock, toBlock]</c> inclusive. Used to detect native TRX transfers, which have no log/event
    /// and so cannot be seen via <c>eth_getLogs</c>. Paginates internally since the node caps how many
    /// blocks one call may return.
    /// </summary>
    Task<IReadOnlyList<TronNativeBlockDto>> GetBlockRangeAsync(
        long fromBlock, long toBlock, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>eth_call</c> against the latest block — invokes a read-only contract method (e.g. <c>balanceOf</c>)
    /// and returns the raw <c>0x</c>-hex return data. Read-only: it sends no key and signs nothing (§10).
    /// </summary>
    Task<string> CallContractAsync(string contractHexAddress, string dataHex, CancellationToken cancellationToken = default);

    /// <summary><c>eth_getBalance</c> at the latest block — an address's native TRX balance in sun (base units).</summary>
    Task<System.Numerics.BigInteger> GetNativeBalanceAsync(string evmHexAddress, CancellationToken cancellationToken = default);
}
