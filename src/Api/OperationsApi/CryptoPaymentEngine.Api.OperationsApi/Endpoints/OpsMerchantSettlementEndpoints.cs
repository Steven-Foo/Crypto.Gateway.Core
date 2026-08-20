using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing merchant policy controls — the write paths that were dev-seed-only/config-only until now: the
/// settlement period (T+N), the whitelisted cash-out wallet, the merchant-withdrawal (cash-out) liquidity cap,
/// and the per-merchant user-withdrawal min/max + approval threshold. Each route's write permission mirrors the
/// permission that reads the same data back: the settlement period + wallet read via the merchant GET
/// (<c>Merchants.*</c>), the cap/limits/threshold via the fees GET (<c>Fees.*</c>). Amounts cross display↔base-unit
/// only here (§14); all bounds live in the domain.
/// </summary>
public static class OpsMerchantSettlementEndpoints
{
    public static void MapOpsMerchantSettlementApi(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/v1/ops/merchants/{id:guid}/settlement-period", SetSettlementPeriodAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/settlement-wallet", SetSettlementWalletAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/withdrawal-cap", SetWithdrawalCapAsync).RequirePermission(OpsPermissions.Fees.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/withdrawal-limits", SetWithdrawalLimitsAsync).RequirePermission(OpsPermissions.Fees.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/approval-threshold", SetApprovalThresholdAsync).RequirePermission(OpsPermissions.Fees.Manage);
    }

    private static async Task<IResult> SetSettlementPeriodAsync(
        Guid id, SetSettlementPeriodRequest request, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.SetSettlementDelayAsync(id, request.Days, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, settlementDelayDays = request.Days },
                error = (string?)null,
            });
    }

    private static async Task<IResult> SetSettlementWalletAsync(
        Guid id, SetSettlementWalletRequest request, IMerchantRegistrar registrar, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad($"Unknown chain '{request.Chain}'.");

        var result = await registrar.SetSettlementWalletAsync(id, chain, request.Address, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, network = chain.ToString(), address = request.Address.Trim() },
                error = (string?)null,
            });
    }

    private static async Task<IResult> SetWithdrawalCapAsync(
        Guid id, SetWithdrawalCapRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad($"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad($"Unknown coin '{request.Coin}' on {chain}.");

        // null flat cap = no flat cap; a zero flat cap is valid (it caps cash-out at zero). Never truncates (§14).
        BigInteger? flatCap = null;
        if (request.FlatCap is { } flat)
        {
            if (flat < 0m)
                return Bad("flatCap cannot be negative.");
            if (flat == 0m)
                flatCap = BigInteger.Zero;
            else if (!AmountConversion.TryToBaseUnits(flat, asset.Decimals, out var baseUnits))
                return Bad("flatCap is finer than the asset's precision.");
            else
                flatCap = baseUnits;
        }

        var result = await policies.SetMerchantWithdrawalCapAsync(
            id, asset.AssetId, flatCap, request.PercentBps, http.RequestAborted);

        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null,
            });
    }

    private static async Task<IResult> SetWithdrawalLimitsAsync(
        Guid id, SetWithdrawalLimitsRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad($"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad($"Unknown coin '{request.Coin}' on {chain}.");

        // null = unset (fall back to the platform config limit); 0 = an explicit "no minimum". Never truncates (§14).
        if (!TryLimitToBase(request.Minimum, asset.Decimals, out var minimum))
            return Bad("minimum is negative or finer than the asset's precision.");
        if (!TryLimitToBase(request.Maximum, asset.Decimals, out var maximum))
            return Bad("maximum is negative or finer than the asset's precision.");

        var result = await policies.SetWithdrawalLimitsAsync(id, asset.AssetId, minimum, maximum, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null,
            });
    }

    private static async Task<IResult> SetApprovalThresholdAsync(
        Guid id, SetApprovalThresholdRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad($"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad($"Unknown coin '{request.Coin}' on {chain}.");

        // null = unset (fall back to the platform config threshold); 0 = an explicit "everything needs approval".
        // Never truncates (§14).
        if (!TryLimitToBase(request.Threshold, asset.Decimals, out var threshold))
            return Bad("threshold is negative or finer than the asset's precision.");

        var result = await policies.SetApprovalThresholdAsync(id, asset.AssetId, threshold, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null,
            });
    }

    /// <summary>A display-unit limit → nullable base units. Null display = unset (null out). Zero is a valid
    /// explicit "no minimum". Refuses a negative or over-precise value — never truncates money (§14).</summary>
    private static bool TryLimitToBase(decimal? display, int decimals, out BigInteger? baseUnits)
    {
        baseUnits = null;
        if (display is not { } value)
            return true; // unset ⇒ fall back to config
        if (value < 0m)
            return false;
        if (value == 0m)
        {
            baseUnits = BigInteger.Zero;
            return true;
        }
        if (!AmountConversion.TryToBaseUnits(value, decimals, out var units))
            return false;
        baseUnits = units;
        return true;
    }

    private static IResult Fail(Error error) =>
        Results.Json(
            new { isSuccess = false, error = error.Message },
            statusCode: error.Type == ErrorType.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
