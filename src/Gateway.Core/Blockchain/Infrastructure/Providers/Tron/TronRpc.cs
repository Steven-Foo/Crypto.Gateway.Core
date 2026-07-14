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
/// response-mapping logic is unit-tested separately via <see cref="ITronRpc"/> fakes.
/// </summary>
public sealed class TronRpc(HttpClient http) : ITronRpc
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

    private static string ToHex(long value) => "0x" + value.ToString("x");
}
