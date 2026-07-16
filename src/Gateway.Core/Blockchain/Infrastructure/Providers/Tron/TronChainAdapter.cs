using System.Numerics;
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
/// EVM-hex recipient to a TRON Base58Check address so the wallet directory can match it. Also detects
/// native TRX transfers (plain <c>TransferContract</c>s have no log/event, so they need native block-tx
/// parsing via <c>/wallet/getblockbylimitnext</c> instead of <c>eth_getLogs</c>) when the asset catalog
/// has a native TRX asset configured — if it doesn't, native scanning is simply skipped, not an error.
/// Confirmation uses block depth (TRON credit policy is confirmation-based); the solidified block is
/// exposed as the finalized height.
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
        var transfers = new List<DetectedTransfer>();

        var contractToAsset = await BuildContractMapAsync(cancellationToken);
        if (contractToAsset.Count > 0)
        {
            var logs = await rpc.GetTransferLogsAsync(fromBlock, toBlock, contractToAsset.Keys, cancellationToken);
            foreach (var log in logs)
            {
                if (TryMapTransfer(log, contractToAsset, out var transfer))
                    transfers.Add(transfer);
            }
        }

        var nativeAssetId = await FindNativeAssetIdAsync(cancellationToken);
        if (nativeAssetId is { } assetId)
        {
            var blocks = await rpc.GetBlockRangeAsync(fromBlock, toBlock, cancellationToken);
            var candidates = blocks.SelectMany(b => ExtractNativeTransfers(b, assetId)).ToList();

            // ExtractNativeTransfers stamps each candidate with the native API's own blockID, but
            // DepositConfirmationService's canonicality check (GetBlockAsync) reads the block hash from the
            // Ethereum-compatible eth_getBlockByNumber — a DIFFERENT encoding of the same block. Comparing
            // across the two would make every native transfer look permanently reorged from the very first
            // confirmation check. Swap in the eth-compatible hash here, fetched only for the (rare) blocks
            // that actually contained a matching transfer.
            foreach (var group in candidates.GroupBy(t => t.BlockNumber))
            {
                var ethBlock = await rpc.GetBlockByNumberAsync(group.Key, cancellationToken);
                if (ethBlock is null)
                {
                    logger.LogWarning("Native transfer(s) at block {BlockNumber} found via the native API but eth_getBlockByNumber returned nothing; skipping this block's transfers.", group.Key);
                    continue;
                }

                transfers.AddRange(group.Select(t => t with { BlockHash = ethBlock.Hash }));
            }
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

    /// <summary>
    /// Extracts every successful native TRX <c>TransferContract</c> from one native-API block.
    /// Pure and unit-tested, like <see cref="TryMapTransfer"/>: this is the money-critical step for
    /// native transfers (recipient address + exact base-unit amount). A transaction whose <c>ret</c>
    /// isn't <c>SUCCESS</c>, a non-transfer contract, or a zero/malformed amount is skipped.
    /// </summary>
    public static IEnumerable<DetectedTransfer> ExtractNativeTransfers(TronNativeBlockDto block, Guid nativeAssetId)
    {
        var blockNumber = block.BlockHeader?.RawData.Number ?? 0;
        var blockHash = block.BlockId;

        foreach (var tx in block.Transactions)
        {
            if (tx.Ret.Count > 0 && !tx.Ret.All(r => string.Equals(r.ContractRet, TronConstants.ContractRetSuccess, StringComparison.Ordinal)))
                continue;

            var contracts = tx.RawData?.Contract ?? [];
            for (var index = 0; index < contracts.Count; index++)
            {
                if (TryMapNativeTransfer(tx.TxId, index, contracts[index], nativeAssetId, blockNumber, blockHash, out var transfer))
                    yield return transfer;
            }
        }
    }

    private static bool TryMapNativeTransfer(
        string txId, int contractIndex, TronNativeContractDto contract, Guid nativeAssetId,
        long blockNumber, string blockHash, out DetectedTransfer transfer)
    {
        transfer = default!;

        if (!string.Equals(contract.Type, TronConstants.TransferContractType, StringComparison.Ordinal))
            return false;

        var value = contract.Parameter?.Value;
        if (value is null || value.Amount <= 0 || string.IsNullOrEmpty(value.ToAddress))
            return false;

        string toAddress;
        try
        {
            toAddress = TronAddress.FromRawHex(value.ToAddress);
        }
        catch (FormatException)
        {
            return false;
        }

        transfer = new DetectedTransfer(
            Chain.Tron,
            toAddress,
            nativeAssetId,
            new BigInteger(value.Amount),
            txId,
            contractIndex,
            blockNumber,
            blockHash);
        return true;
    }

    private async Task<Guid?> FindNativeAssetIdAsync(CancellationToken cancellationToken)
    {
        var assets = await assetCatalog.GetActiveAsync(cancellationToken);
        return assets.FirstOrDefault(a => a.Chain == Chain.Tron && a.IsNative)?.AssetId;
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
