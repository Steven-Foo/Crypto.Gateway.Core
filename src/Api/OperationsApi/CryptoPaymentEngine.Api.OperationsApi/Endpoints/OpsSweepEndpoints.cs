using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing sweep state read — the back-office view of the concentration path (deposit address → cold
/// treasury). A pure read over the sweep state machine the money host owns (§4.7 — this host runs no scan/
/// sign/broadcast worker); it moves nothing and holds no keys. Amounts are shown as a display value plus the
/// exact base-unit integer (§14). Authenticated staff with <c>ops.sweep.view</c>.
/// </summary>
public static class OpsSweepEndpoints
{
    private static readonly string[] Statuses = ["Pending", "Signing", "Broadcast", "Confirmed", "Failed"];

    public static void MapOpsSweepApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/sweeps", ListAsync).RequirePermission(OpsPermissions.Sweep.View);

    private static async Task<IResult> ListAsync(
        ISweepDirectory sweeps,
        IAssetCatalog assets,
        HttpContext http,
        string? chain = null,
        string? status = null,
        Guid? walletId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        Chain? chainFilter = null;
        if (!string.IsNullOrWhiteSpace(chain))
        {
            if (!Enum.TryParse<Chain>(chain, ignoreCase: true, out var parsed))
                return Bad($"Unknown chain '{chain}'.");
            chainFilter = parsed;
        }

        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = Statuses.FirstOrDefault(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
            if (normalisedStatus is null)
                return Bad($"Unknown status '{status}'. Expected one of: {string.Join(", ", Statuses)}.");
        }

        var filter = new SweepAdminFilter(chainFilter, normalisedStatus, walletId, AssetId: null, fromDate, toDate);
        var (items, total) = await sweeps.SearchAsync(filter, page, pageSize, http.RequestAborted);
        var summary = await sweeps.GetStatusCountsAsync(chainFilter, http.RequestAborted);

        var decimalsByAsset = new Dictionary<Guid, int>();
        var rows = new List<object>(items.Count);
        foreach (var s in items)
        {
            if (!decimalsByAsset.TryGetValue(s.AssetId, out var decimals))
            {
                var asset = await assets.FindByIdAsync(s.AssetId, http.RequestAborted);
                decimals = asset?.Decimals ?? 6;
                decimalsByAsset[s.AssetId] = decimals;
            }

            rows.Add(new
            {
                sweepId = s.SweepId,
                walletId = s.WalletId,
                chain = s.Chain,
                assetId = s.AssetId,
                fromAddress = s.FromAddress,
                toAddress = s.ToAddress,
                amount = AmountConversion.ToDisplay(BigInteger.Parse(s.AmountBaseUnits), decimals),
                amountBaseUnits = s.AmountBaseUnits,
                status = s.Status,
                txHash = s.TransactionHash,
                confirmations = s.Confirmations,
                failureReason = s.FailureReason,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt,
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, summary, items = rows },
            error = (string?)null,
        });
    }

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
