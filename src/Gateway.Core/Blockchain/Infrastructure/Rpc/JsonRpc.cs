using System.Net.Http.Json;
using System.Text.Json;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Rpc;

/// <summary>Raised when a JSON-RPC endpoint returns an <c>error</c> object or an unparseable response.</summary>
public sealed class JsonRpcException(string method, string detail) : Exception($"JSON-RPC '{method}' failed: {detail}")
{
    public string Method { get; } = method;
}

/// <summary>
/// Minimal, transport-only JSON-RPC 2.0 helper over an already-configured (and resilience-wrapped)
/// <see cref="HttpClient"/>. Read-only by design — the caller supplies the method and params; nothing
/// here touches keys (§10). Returns the raw <c>result</c> element for the caller to shape into a DTO.
/// </summary>
public static class JsonRpc
{
    public static async Task<JsonElement> InvokeAsync(
        HttpClient http, string path, string method, object?[] parameters, CancellationToken cancellationToken = default)
    {
        // Anonymous object → verbatim property names: jsonrpc / id / method / params (the JSON-RPC 2.0 envelope).
        var request = new { jsonrpc = "2.0", id = 1, method, @params = parameters };

        using var response = await http.PostAsJsonAsync(path, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        if (document.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null)
            throw new JsonRpcException(method, error.GetRawText());

        if (!document.TryGetProperty("result", out var result))
            throw new JsonRpcException(method, "response contained no 'result'.");

        return result.Clone();
    }
}
