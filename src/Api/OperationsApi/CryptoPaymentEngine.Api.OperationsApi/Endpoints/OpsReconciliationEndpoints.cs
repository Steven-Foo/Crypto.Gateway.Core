using System.Globalization;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing custody-status read: the latest reconciliation snapshot per (chain, asset) — the ledger's
/// <c>TreasuryAsset</c> holding vs the summed on-chain balance across every controlled address, and their drift.
/// A pure read over the observability snapshots the money host's reconciliation worker writes to MongoDB (§2);
/// this host neither computes nor writes them. Non-Balanced rows (Drift / Incomplete) sort first so an operator
/// sees problems at the top. Authenticated staff only (no Admin gate — it moves nothing).
/// </summary>
public static class OpsReconciliationEndpoints
{
    public static void MapOpsReconciliationApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/reconciliation", ListAsync);

    private static async Task<IResult> ListAsync(
        IReconciliationStore store, IAssetCatalog assets, HttpContext http, string? chain = null)
    {
        Chain? filter = null;
        if (!string.IsNullOrWhiteSpace(chain))
        {
            if (!Enum.TryParse<Chain>(chain, ignoreCase: true, out var parsed))
                return Results.Json(new { isSuccess = false, error = $"Unknown chain '{chain}'." }, statusCode: StatusCodes.Status400BadRequest);
            filter = parsed;
        }

        var snapshots = await store.ListAsync(http.RequestAborted);

        var ordered = snapshots
            .Where(s => filter is not { } c || s.Chain == c)
            .OrderBy(s => s.Status == ReconciliationStatus.Balanced) // false (Drift/Incomplete) first
            .ThenBy(s => s.Chain)
            .ThenBy(s => s.AssetSymbol)
            .ToList();

        var decimalsByAsset = new Dictionary<Guid, int>();
        var rows = new List<object>(ordered.Count);
        foreach (var s in ordered)
        {
            if (!decimalsByAsset.TryGetValue(s.AssetId, out var decimals))
            {
                var asset = await assets.FindByIdAsync(s.AssetId, http.RequestAborted);
                decimals = asset?.Decimals ?? 6;
                decimalsByAsset[s.AssetId] = decimals;
            }

            rows.Add(new
            {
                chain = s.Chain.ToString(),
                assetId = s.AssetId,
                coin = s.AssetSymbol,
                status = s.Status.ToString(),
                ledgerHolding = AmountConversion.ToDisplay(s.LedgerHolding, decimals),
                onChainTotal = AmountConversion.ToDisplay(s.OnChainTotal, decimals),
                drift = AmountConversion.ToDisplay(s.Drift, decimals),
                // The exact base-unit drift too — a custody audit needs the precise integer, which a display
                // decimal cannot always carry (§14).
                driftBaseUnits = s.Drift.ToString(CultureInfo.InvariantCulture),
                addressesScanned = s.AddressesScanned,
                addressesUnreadable = s.AddressesUnreadable,
                observedAt = s.ObservedAt,
            });
        }

        return Results.Ok(new { isSuccess = true, data = new { items = rows }, error = (string?)null });
    }
}
