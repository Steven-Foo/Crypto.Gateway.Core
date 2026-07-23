using System.Net.Http.Json;
using System.Text.Json;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;

/// <summary>
/// TRON node access over TronGrid / full-node HTTP. Uses TRON's Ethereum-compatible JSON-RPC
/// (<c>/jsonrpc</c>) for blocks and logs, and the native <c>/walletsolidity/getnowblock</c> for the
/// solidified (irreversible) height, which the eth JSON-RPC does not expose. The <see cref="HttpClient"/>
/// is a typed client the DI layer wires with the RPC base URL and a standard resilience handler.
///
/// NOTE: exercised against a live node only in staging (needs an endpoint/API key). The adapter's
/// response-mapping logic is unit-tested separately via <see cref="ITronRpc"/>/<see cref="ITronTxRpc"/> fakes.
/// </summary>
public sealed class TronRpc(HttpClient http) : ITronRpc, ITronTxRpc
{
    public async Task<long> GetBlockNumberAsync(CancellationToken cancellationToken = default)
    {
        var result = await JsonRpc.InvokeAsync(http, "jsonrpc", "eth_blockNumber", [], cancellationToken);
        return HexNumber.ToInt64(result.GetString() ?? "0x0");
    }

    public async Task<TronBlockDto?> GetBlockByNumberAsync(long blockNumber, CancellationToken cancellationToken = default)
    {
        var result = await JsonRpc.InvokeAsync(
            http, "jsonrpc", "eth_getBlockByNumber", [ToHex(blockNumber), false], cancellationToken);

        return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : result.Deserialize<TronBlockDto>();
    }

    public async Task<IReadOnlyList<TronLogDto>> GetTransferLogsAsync(
        long fromBlock, long toBlock, IReadOnlyCollection<string> contractHexAddresses, CancellationToken cancellationToken = default)
    {
        var filter = new
        {
            fromBlock = ToHex(fromBlock),
            toBlock = ToHex(toBlock),
            address = contractHexAddresses.Select(a => a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a : "0x" + a).ToArray(),
            topics = new[] { TronConstants.TransferEventSignature },
        };

        var result = await JsonRpc.InvokeAsync(http, "jsonrpc", "eth_getLogs", [filter], cancellationToken);
        return result.Deserialize<List<TronLogDto>>() ?? [];
    }

    public async Task<long> GetSolidifiedBlockNumberAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("walletsolidity/getnowblock", new { }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return document.TryGetProperty("block_header", out var header)
               && header.TryGetProperty("raw_data", out var raw)
               && raw.TryGetProperty("number", out var number)
            ? number.GetInt64()
            : 0L;
    }

    // TRON's node caps how many blocks /wallet/getblockbylimitnext returns per call; page through the
    // requested range rather than assuming it fits in one request.
    private const int BlockRangePageSize = 100;

    public async Task<IReadOnlyList<TronNativeBlockDto>> GetBlockRangeAsync(
        long fromBlock, long toBlock, CancellationToken cancellationToken = default)
    {
        var blocks = new List<TronNativeBlockDto>();
        var start = fromBlock;

        while (start <= toBlock)
        {
            // getblockbylimitnext takes [startNum, endNum) — startNum inclusive, endNum EXCLUSIVE.
            var end = Math.Min(start + BlockRangePageSize, toBlock + 1);

            using var response = await http.PostAsJsonAsync(
                "wallet/getblockbylimitnext", new { startNum = start, endNum = end }, cancellationToken);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<TronBlockRangeResponseDto>(cancellationToken);
            if (page?.Block is { Count: > 0 })
                blocks.AddRange(page.Block);

            start = end;
        }

        return blocks;
    }

    // ── Write path (ITronTxRpc): native /wallet/* API, keyless (§10) ──

    public async Task<TronTriggerResultDto> TriggerSmartContractAsync(
        TriggerSmartContractRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("wallet/triggersmartcontract", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TronTriggerResultDto>(cancellationToken)
            ?? throw new JsonRpcException("triggersmartcontract", "empty response.");
    }

    public async Task<TronBroadcastResultDto> BroadcastTransactionAsync(
        JsonElement signedTransaction, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("wallet/broadcasttransaction", signedTransaction, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TronBroadcastResultDto>(cancellationToken)
            ?? throw new JsonRpcException("broadcasttransaction", "empty response.");
    }

    public async Task<TronTransactionInfoDto?> GetTransactionInfoAsync(
        string transactionId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "wallet/gettransactioninfobyid", new { value = transactionId }, cancellationToken);
        response.EnsureSuccessStatusCode();

        var element = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        // An empty object {} means the transaction is not yet in a block (or unknown/dropped).
        if (element.ValueKind is not JsonValueKind.Object)
            return null;
        using var enumerator = element.EnumerateObject();
        return enumerator.MoveNext() ? element.Deserialize<TronTransactionInfoDto>() : null;
    }

    private static string ToHex(long value) => "0x" + value.ToString("x");
}
