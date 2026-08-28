using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-initiated manual credit/debit to a merchant's ledger balance — a compensating correction, not backed
/// by a real on-chain deposit/withdrawal (e.g. a support-ticket goodwill credit, or clawing back an over-credit).
/// Gated on <see cref="OpsPermissions.Balances.Adjust"/>, deliberately granted to Admin only (never bundled with
/// <see cref="OpsPermissions.Merchants.Manage"/>): this moves real merchant liability, unlike every other merchant
/// admin action. Posts immediately — no second-approver threshold (may be revisited if adjustment volume/size
/// grows). <see cref="LedgerPoster"/> keeps the money-critical accounting policy (which accounts move, and why a
/// manual correction never touches <c>TreasuryAsset</c>); this endpoint only converts display↔base units at the
/// edge (§14) and enforces the mandatory <c>reason</c> for audit.
/// </summary>
public static class OpsMerchantBalanceEndpoints
{
    public static void MapOpsMerchantBalanceApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/merchants/{id:guid}/balance/credit", CreditAsync).RequirePermission(OpsPermissions.Balances.Adjust);
        app.MapPost("/api/v1/ops/merchants/{id:guid}/balance/debit", DebitAsync).RequirePermission(OpsPermissions.Balances.Adjust);
    }

    private static async Task<IResult> CreditAsync(
        Guid id, AdjustMerchantBalanceRequest request, IMerchantRegistrar registrar, IAssetCatalog assets,
        ILedgerPoster ledger, IAuditLogger audit, HttpContext http)
    {
        var (asset, amount, error) = await ResolveAsync(id, request, registrar, assets, http);
        if (error is not null)
            return error;

        var result = await ledger.CreditMerchantBalanceAsync(
            new CreditMerchantBalanceCommand(id, asset!.AssetId, amount, request.Reason, request.AdjustmentId),
            http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.balance_credited", "Merchant", id.ToString(),
            $"{request.Amount} {asset.Symbol} ({asset.Chain}): {request.Reason}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = asset.Chain.ToString(), outcome = result.Value.ToString() },
            error = (string?)null,
        });
    }

    private static async Task<IResult> DebitAsync(
        Guid id, AdjustMerchantBalanceRequest request, IMerchantRegistrar registrar, IAssetCatalog assets,
        ILedgerPoster ledger, IAuditLogger audit, HttpContext http)
    {
        var (asset, amount, error) = await ResolveAsync(id, request, registrar, assets, http);
        if (error is not null)
            return error;

        var result = await ledger.DebitMerchantBalanceAsync(
            new DebitMerchantBalanceCommand(id, asset!.AssetId, amount, request.Reason, request.AdjustmentId),
            http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.balance_debited", "Merchant", id.ToString(),
            $"{request.Amount} {asset.Symbol} ({asset.Chain}): {request.Reason}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = asset.Chain.ToString(), outcome = result.Value.ToString() },
            error = (string?)null,
        });
    }

    /// <summary>Shared validation: merchant exists, chain/coin resolve to a known asset, amount is a storable
    /// positive base-unit value at the asset's precision. Returns a non-null <c>Error</c> IResult on any failure.</summary>
    private static async Task<(AssetDto? Asset, System.Numerics.BigInteger Amount, IResult? Error)> ResolveAsync(
        Guid merchantId, AdjustMerchantBalanceRequest request, IMerchantRegistrar registrar, IAssetCatalog assets, HttpContext http)
    {
        var merchant = await registrar.GetAsync(merchantId, http.RequestAborted);
        if (merchant.IsFailure)
            return (null, default, Results.Json(new { isSuccess = false, error = "Merchant not found." }, statusCode: StatusCodes.Status404NotFound));

        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return (null, default, Bad($"Unknown chain '{request.Chain}'."));

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return (null, default, Bad($"Unknown coin '{request.Coin}' on {chain}."));

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var amount))
            return (null, default, Bad("amount must be positive and no finer than the asset's precision."));

        return (asset, amount, null);
    }

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

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
