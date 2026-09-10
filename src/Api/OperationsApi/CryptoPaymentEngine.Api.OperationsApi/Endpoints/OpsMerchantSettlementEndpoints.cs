using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
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

        // Turns the merchant's OWN payout-approval stage on/off. Merchants.Manage rather than Fees.Manage:
        // it controls the merchant's process, not its pricing.
        app.MapPut("/api/v1/ops/merchants/{id:guid}/payout-approval", SetPayoutApprovalAsync).RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/withdrawal-cap", SetWithdrawalCapAsync).RequirePermission(OpsPermissions.Fees.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/withdrawal-limits", SetWithdrawalLimitsAsync).RequirePermission(OpsPermissions.Fees.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/deposit-limits", SetDepositLimitsAsync).RequirePermission(OpsPermissions.Fees.Manage);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/approval-threshold", SetApprovalThresholdAsync).RequirePermission(OpsPermissions.Fees.Manage);
    }

    /// <summary>
    /// Enables or disables the merchant's own payout-approval stage. When on, that merchant's user payouts
    /// stop in <c>PendingMerchantApproval</c> for its approver before the platform evaluates them — on BOTH
    /// the HMAC API and the portal, because this is merchant policy rather than a property of the caller.
    ///
    /// <para><b>This changes where a merchant's money stops</b>, so a UI should confirm before calling.
    /// Turning it ON for a merchant with no approver configured will leave payouts waiting indefinitely;
    /// turning it OFF releases nothing already waiting — those still need approving or rejecting.</para>
    ///
    /// <para>It can never weaken the platform gate: the approval threshold is re-resolved server-side when the
    /// merchant approves, so a merchant cannot approve its way past a payout that needs staff.</para>
    /// </summary>
    private static async Task<IResult> SetPayoutApprovalAsync(
        Guid id, SetPayoutApprovalRequest request, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.SetRequiresPayoutApprovalAsync(id, request.Required, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.payout_approval_changed", "Merchant", id.ToString(),
            request.Required ? "enabled" : "disabled", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { merchantId = id, requiresPayoutApproval = request.Required },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static async Task<IResult> SetSettlementPeriodAsync(
        Guid id, SetSettlementPeriodRequest request, IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.SetSettlementDelayAsync(id, request.Days, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, settlementDelayDays = request.Days },
                error = (string?)null, errorCode = (string?)null,
            });
    }

    private static async Task<IResult> SetSettlementWalletAsync(
        Guid id, SetSettlementWalletRequest request, IMerchantRegistrar registrar, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var result = await registrar.SetSettlementWalletAsync(id, chain, request.Address, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, network = chain.ToString(), address = request.Address.Trim() },
                error = (string?)null, errorCode = (string?)null,
            });
    }

    private static async Task<IResult> SetWithdrawalCapAsync(
        Guid id, SetWithdrawalCapRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        // null flat cap = no flat cap; a zero flat cap is valid (it caps cash-out at zero). Never truncates (§14).
        BigInteger? flatCap = null;
        if (request.FlatCap is { } flat)
        {
            if (flat < 0m)
                return Bad(OpsErrorCodes.InvalidAmount, "flatCap cannot be negative.");
            if (flat == 0m)
                flatCap = BigInteger.Zero;
            else if (!AmountConversion.TryToBaseUnits(flat, asset.Decimals, out var baseUnits))
                return Bad(OpsErrorCodes.InvalidAmount, "flatCap is finer than the asset's precision.");
            else
                flatCap = baseUnits;
        }

        if (!OpsPercent.TryToBps(request.Percent, out var percentBps))
            return Bad(OpsErrorCodes.InvalidAmount, "percent must be non-negative, at most 100%, and at most 2 decimal places.");

        var result = await policies.SetMerchantWithdrawalCapAsync(
            id, asset.AssetId, flatCap, percentBps, http.RequestAborted);

        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null, errorCode = (string?)null,
            });
    }

    private static async Task<IResult> SetWithdrawalLimitsAsync(
        Guid id, SetWithdrawalLimitsRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        // null = unset (fall back to the platform config limit); 0 = an explicit "no minimum". Never truncates (§14).
        if (!TryLimitToBase(request.Minimum, asset.Decimals, out var minimum))
            return Bad(OpsErrorCodes.InvalidAmount, "minimum is negative or finer than the asset's precision.");
        if (!TryLimitToBase(request.Maximum, asset.Decimals, out var maximum))
            return Bad(OpsErrorCodes.InvalidAmount, "maximum is negative or finer than the asset's precision.");

        var result = await policies.SetWithdrawalLimitsAsync(id, asset.AssetId, minimum, maximum, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null, errorCode = (string?)null,
            });
    }

    private static async Task<IResult> SetDepositLimitsAsync(
        Guid id, SetDepositLimitsRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        // null = unset (min falls back to the platform dust-floor config, max stays unbounded); 0 = an explicit
        // "no minimum". Never truncates (§14).
        if (!TryLimitToBase(request.Minimum, asset.Decimals, out var minimum))
            return Bad(OpsErrorCodes.InvalidAmount, "minimum is negative or finer than the asset's precision.");
        if (!TryLimitToBase(request.Maximum, asset.Decimals, out var maximum))
            return Bad(OpsErrorCodes.InvalidAmount, "maximum is negative or finer than the asset's precision.");

        var result = await policies.SetDepositLimitsAsync(id, asset.AssetId, minimum, maximum, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null, errorCode = (string?)null,
            });
    }

    private static async Task<IResult> SetApprovalThresholdAsync(
        Guid id, SetApprovalThresholdRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        // null = unset (fall back to the platform config threshold); 0 = an explicit "everything needs approval".
        // Never truncates (§14).
        if (!TryLimitToBase(request.Threshold, asset.Decimals, out var threshold))
            return Bad(OpsErrorCodes.InvalidAmount, "threshold is negative or finer than the asset's precision.");

        var result = await policies.SetApprovalThresholdAsync(id, asset.AssetId, threshold, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new
            {
                isSuccess = true,
                data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
                error = (string?)null, errorCode = (string?)null,
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

    private static IResult Bad(string errorCode, string message) => OpsResults.Bad(errorCode, message);
}
