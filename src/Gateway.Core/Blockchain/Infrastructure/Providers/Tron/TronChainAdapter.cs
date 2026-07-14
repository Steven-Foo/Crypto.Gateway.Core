using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;
using CryptoPaymentEngine.SharedKernel;
using Microsoft.Extensions.Logging;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// TRON read-only chain adapter. Detects TRC-20 (e.g. USDT) deposits via <c>eth_getLogs</c> Transfer
/// events, resolves each token contract to an <c>AssetId</c> through the asset catalog, and converts the
/// EVM-hex recipient to a TRON Base58Check address so the wallet directory can match it. Confirmation
/// uses block depth (TRON credit policy is confirmation-based); the solidified block is exposed as the
/// finalized height.
///
/// NOTE: native TRX (non-contract) transfers are NOT yet detected here — that needs native block-tx
/// parsing (<c>/wallet/getblockbynum</c>, TransferContract). TRC-20 is the primary deposit asset; native
/// TRX detection is a documented follow-up.
/// </summary>
public sealed class TronChainAdapter(ITronRpc rpc, IAssetCatalog assetCatalog, ILogger<TronChainAdapter> logger) : IChainAdapter
{
    public Chain Chain => Chain.Tron;

    public Task<long> GetTipHeightAsync(Chain chain, CancellationToken cancellationToken = default) =>
        rpc.GetBlockNumberAsync(cancellationToken);

    public async Task<BlockRef?> GetBlockAsync(Chain chain, long blockNumber, CancellationToken cancellationToken = default)
    {
        var block = await rpc.GetBlockByNumberAsync(blockNumber, cancellationToken);
        return block is null ? null : new BlockRef(HexNumber.ToInt64(block.Number), block.Hash);
    }

    public Task<long> GetFinalizedHeightAsync(Chain chain, CancellationToken cancellationToken = default) =>
        rpc.GetSolidifiedBlockNumberAsync(cancellationToken);

    public async Task<IReadOnlyList<DetectedTransfer>> ScanAsync(
        Chain chain, long fromBlock, long toBlock, CancellationToken cancellationToken = default)
    {
        var contractToAsset = await BuildContractMapAsync(cancellationToken);
        if (contractToAsset.Count == 0)
            return [];

        var logs = await rpc.GetTransferLogsAsync(fromBlock, toBlock, contractToAsset.Keys, cancellationToken);

        var transfers = new List<DetectedTransfer>(logs.Count);
        foreach (var log in logs)
        {
            if (TryMapTransfer(log, contractToAsset, out var transfer))
                transfers.Add(transfer);
        }

        return transfers;
    }

    /// <summary>
    /// Maps one TRC-20 Transfer log to a <see cref="DetectedTransfer"/>. Pure and unit-tested: it is the
    /// money-critical step (recipient address + exact base-unit amount). Returns false for non-Transfer
    /// logs, unknown contracts, or zero amounts.
    /// </summary>
    public static bool TryMapTransfer(
        TronLogDto log, IReadOnlyDictionary<string, Guid> contractToAsset, out DetectedTransfer transfer)
    {
        transfer = default!;

        // Transfer(from, to, value): topic0 = signature, topic1 = from, topic2 = to.
        if (log.Topics.Length != 3 ||
            !string.Equals(log.Topics[0], TronConstants.TransferEventSignature, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!contractToAsset.TryGetValue(NormalizeHex(log.Address), out var assetId))
            return false;

        var amount = HexNumber.ToBigInteger(log.Data);
        if (amount <= System.Numerics.BigInteger.Zero)
            return false;

        transfer = new DetectedTransfer(
            Chain.Tron,
            TronAddress.FromEvmTopic(log.Topics[2]),
            assetId,
            amount,
            log.TransactionHash,
            (int)HexNumber.ToInt64(log.LogIndex),
            HexNumber.ToInt64(log.BlockNumber),
            log.BlockHash);
        return true;
    }

    private async Task<Dictionary<string, Guid>> BuildContractMapAsync(CancellationToken cancellationToken)
    {
        var assets = await assetCatalog.GetActiveAsync(cancellationToken);
        var map = new Dictionary<string, Guid>();

        foreach (var asset in assets.Where(a => a.Chain == Chain.Tron && !a.IsNative && a.ContractAddress is not null))
        {
            try
            {
                map[TronAddress.ToEvmHex(asset.ContractAddress!)] = asset.AssetId;
            }
            catch (FormatException ex)
            {
                logger.LogWarning(ex, "Skipping asset {AssetId}: contract address is not a valid TRON address.", asset.AssetId);
            }
        }

        return map;
    }

    private static string NormalizeHex(string hex)
    {
        var span = hex.AsSpan().Trim();
        if (span.StartsWith("0x") || span.StartsWith("0X"))
            span = span[2..];
        return span.ToString().ToLowerInvariant();
    }
}
