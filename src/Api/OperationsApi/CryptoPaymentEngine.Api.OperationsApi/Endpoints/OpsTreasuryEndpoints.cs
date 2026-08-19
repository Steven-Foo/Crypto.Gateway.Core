using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Treasury.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff (Admin) actions for the three-tier custody COLD RELOAD (cold treasury → hot pool) — the production
/// counterpart of MerchantGateway's dev-only <c>/dev/treasury/*</c> helpers. Human-in-the-loop and <b>keyless</b>:
/// the ops host builds the <em>unsigned</em> treasury→hot transfer, the operator signs with the cold key
/// CLIENT-SIDE (never sent to any backend, §10), and the money host's <c>TreasuryReloadWorker</c> broadcasts +
/// confirms it. It posts <b>no ledger entry</b> — the reload moves funds between two platform-controlled
/// addresses, so total custody is unchanged (§14). Every route is Admin-only.
/// </summary>
public static class OpsTreasuryEndpoints
{
    public static void MapOpsTreasuryApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/treasury/hot-pool", HotPoolAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/cold-wallet", RegisterColdAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/reload", InitiateReloadAsync).RequirePermission(OpsPermissions.Treasury.Manage);
        app.MapPost("/api/v1/ops/treasury/reload/{reloadId:guid}/submit", SubmitReloadAsync).RequirePermission(OpsPermissions.Treasury.Manage);
    }

    /// <summary>Lists the hot-pool wallets so the operator can pick a reload target. The signing
    /// <c>KeyReference</c> is deliberately never returned (§10) — only the wallet id + address.</summary>
    private static async Task<IResult> HotPoolAsync(
        string chain, ITreasuryHotWalletDirectory hotWallets, HttpContext http)
    {
        if (!TryChain(chain, out var parsed))
            return BadChain(chain);

        var pool = await hotWallets.GetHotWalletPoolAsync(parsed, http.RequestAborted);
        return Ok(pool.Select(w => new { w.WalletId, w.Address }).ToList());
    }

    private static async Task<IResult> RegisterColdAsync(
        RegisterColdWalletRequest request, ITreasuryColdWalletRegistrar registrar, HttpContext http)
    {
        if (!TryChain(request.Chain, out var parsed))
            return BadChain(request.Chain);

        var result = await registrar.RegisterAsync(parsed, request.Address, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Ok(new { chain = parsed.ToString(), address = request.Address, registered = true });
    }

    /// <summary>Builds the unsigned treasury→hot transfer for the operator to sign client-side. Returns the
    /// reload id + the unsigned transaction hex; no key is involved (§10).</summary>
    private static async Task<IResult> InitiateReloadAsync(
        InitiateReloadRequest request, IAssetCatalog assets, ITreasuryReloadService reloads, HttpContext http)
    {
        if (!TryChain(request.Chain, out var parsed))
            return BadChain(request.Chain);

        // USDT-only first cut; the catalog gives the asset id + decimals for the §14 display→base conversion.
        var asset = await assets.FindAsync(parsed, "USDT", http.RequestAborted);
        if (asset is null)
            return Error($"No USDT asset configured for {parsed}.");

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var baseUnits))
            return Error("Amount must be positive and within the asset's supported precision.");

        var result = await reloads.InitiateAsync(parsed, asset.AssetId, request.TargetWalletId, baseUnits, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Ok(new { reloadId = result.Value.ReloadId, unsignedTransactionHex = result.Value.UnsignedTransactionHex });
    }

    private static async Task<IResult> SubmitReloadAsync(
        Guid reloadId, SubmitReloadRequest request, ITreasuryReloadService reloads, HttpContext http)
    {
        byte[] signed;
        try { signed = Convert.FromHexString(request.SignedHex); }
        catch (FormatException) { return Error("signedHex must be valid hex."); }

        var result = await reloads.SubmitSignedAsync(reloadId, signed, http.RequestAborted);
        return result.IsFailure ? Fail(result.Error!) : Ok(new { reloadId, submitted = true });
    }

    private static bool TryChain(string chain, out Chain parsed) => Enum.TryParse(chain, ignoreCase: true, out parsed);

    private static IResult BadChain(string chain) => Error($"Unknown chain '{chain}'.");

    private static IResult Ok(object data) => Results.Ok(new { isSuccess = true, data, error = (string?)null });

    private static IResult Error(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Fail(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new { isSuccess = false, error = error.Message }, statusCode: status);
    }
}
