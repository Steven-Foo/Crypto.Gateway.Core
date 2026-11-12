using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// The PLATFORM DEFAULTS, per asset: the fees and limits a merchant gets wherever it has no value of its own.
/// Precedence everywhere is merchant's own value → these defaults → deployed configuration. Saving here never
/// edits any merchant's own values. The create-merchant screen also uses the fees to pre-fill its inputs.
/// </summary>
public static class OpsDefaultFeeEndpoints
{
    public static void MapOpsDefaultFeeApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/merchants/default-fees", ListAsync).RequirePermission(OpsPermissions.Fees.View);
        app.MapPut("/api/v1/ops/merchants/default-fees", SetAsync).RequirePermission(OpsPermissions.Fees.Manage)
            .RequireTwoFactor(GuardedActions.MerchantPricing);
        app.MapPut("/api/v1/ops/merchants/default-limits", SetLimitsAsync).RequirePermission(OpsPermissions.Fees.Manage)
            .RequireTwoFactor(GuardedActions.MerchantPricing);
    }

    private static async Task<IResult> SetLimitsAsync(
        SetDefaultLimitsRequest request, IDefaultFeePolicyService defaults, IAssetCatalog assets,
        IAuditLogger audit, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain) || int.TryParse(request.Chain, out _))
            return OpsResults.Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, (request.Coin ?? "").Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return OpsResults.Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        var values = new BigInteger?[5];
        var inputs = new (decimal? Value, string Name)[]
        {
            (request.MinimumDeposit, "minimumDeposit"), (request.MaximumDeposit, "maximumDeposit"),
            (request.MinimumWithdrawal, "minimumWithdrawal"), (request.MaximumWithdrawal, "maximumWithdrawal"),
            (request.ApprovalThreshold, "approvalThreshold"),
        };
        for (var i = 0; i < inputs.Length; i++)
        {
            if (inputs[i].Value is not { } display)
                continue;
            if (display < 0m || !TryFeeToBase(display, asset.Decimals, out var converted))
                return OpsResults.Bad(OpsErrorCodes.InvalidAmount, $"{inputs[i].Name} is negative or finer than the asset's precision.");
            values[i] = converted;
        }

        var result = await defaults.SetLimitsAsync(
            asset.AssetId, new DefaultLimits(values[0], values[1], values[2], values[3], values[4]), http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.default_limits_updated", "DefaultFeePolicy", asset.AssetId.ToString(),
            $"{asset.Symbol} ({chain}): deposit {Show(request.MinimumDeposit)}-{Show(request.MaximumDeposit)}, "
            + $"payout {Show(request.MinimumWithdrawal)}-{Show(request.MaximumWithdrawal)}, approval>{Show(request.ApprovalThreshold)}",
            actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = Row(result.Value, chain, asset.Symbol, asset.Decimals), error = (string?)null, errorCode = (string?)null });
    }

    private static string Show(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "config";

    /// <summary>One asset's defaults as the screens show them. A null limit = no platform default for it.</summary>
    private static object Row(DefaultFeePolicyView p, Chain? chain, string? coin, int decimals) => new
    {
        chain = chain?.ToString(),
        coin,
        depositFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.DepositFeeFixed), decimals),
        depositFeePercent = OpsPercent.ToPercent(p.DepositFeeBps),
        depositFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(p.MinimumDepositFee), decimals),
        withdrawalFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.WithdrawalFee), decimals),
        withdrawalFeePercent = OpsPercent.ToPercent(p.WithdrawalFeeBps),
        withdrawalFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(p.MinimumWithdrawalFee), decimals),
        minimumDeposit = Limit(p.MinimumDeposit, decimals),
        maximumDeposit = Limit(p.MaximumDeposit, decimals),
        minimumWithdrawal = Limit(p.MinimumWithdrawal, decimals),
        maximumWithdrawal = Limit(p.MaximumWithdrawal, decimals),
        approvalThreshold = Limit(p.ApprovalThreshold, decimals),
    };

    private static decimal? Limit(string? baseUnits, int decimals) =>
        baseUnits is null ? null : AmountConversion.ToDisplay(BigInteger.Parse(baseUnits), decimals);

    private static async Task<IResult> ListAsync(
        IDefaultFeePolicyService defaults, IAssetCatalog assets, HttpContext http)
    {
        var result = await defaults.ListAsync(http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var rows = new List<object>(result.Value.Count);
        foreach (var p in result.Value)
        {
            var asset = await assets.FindByIdAsync(p.AssetId, http.RequestAborted);
            rows.Add(Row(p, asset?.Chain, asset?.Symbol, asset?.Decimals ?? 6));
        }

        return Results.Ok(new { isSuccess = true, data = new { defaults = rows }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> SetAsync(
        SetDefaultFeeRequest request, IDefaultFeePolicyService defaults, IAssetCatalog assets,
        IAuditLogger audit, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return OpsResults.Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return OpsResults.Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        if (!OpsPercent.TryToBps(request.DepositFeePercent, out var depositBps))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "depositFeePercent must be non-negative, at most 100%, and at most 2 decimal places.");
        if (!OpsPercent.TryToBps(request.WithdrawalFeePercent, out var withdrawalBps))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "withdrawalFeePercent must be non-negative, at most 100%, and at most 2 decimal places.");

        if (!TryFeeToBase(request.DepositFeeFixed, asset.Decimals, out var depositFixed))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "depositFeeFixed is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.DepositFeeMinimum, asset.Decimals, out var depositMinimum))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "depositFeeMinimum is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.WithdrawalFeeFixed, asset.Decimals, out var withdrawalFixed))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "withdrawalFeeFixed is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.WithdrawalFeeMinimum, asset.Decimals, out var withdrawalMinimum))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "withdrawalFeeMinimum is negative or finer than the asset's precision.");

        var result = await defaults.SetAsync(
            asset.AssetId, depositFixed, depositBps, depositMinimum, withdrawalFixed, withdrawalBps, withdrawalMinimum,
            http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.default_fee_updated", "DefaultFeePolicy", asset.AssetId.ToString(),
            $"{asset.Symbol} ({chain}): deposit={request.DepositFeeFixed}+{request.DepositFeePercent}%(min {request.DepositFeeMinimum}), "
            + $"withdrawal={request.WithdrawalFeeFixed}+{request.WithdrawalFeePercent}%(min {request.WithdrawalFeeMinimum})",
            actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = Row(result.Value, chain, asset.Symbol, asset.Decimals),
            error = (string?)null, errorCode = (string?)null,
        });
    }

    /// <summary>Same rule as <c>OpsMerchantFeeEndpoints</c>'s helper of the same name: a zero fixed/minimum fee
    /// is valid (a pure-percentage fee), but this endpoint has no "omit = unchanged" concept, so unlike that
    /// one this always takes a value, never a null.</summary>
    private static bool TryFeeToBase(decimal display, int decimals, out BigInteger baseUnits)
    {
        if (display == 0m)
        {
            baseUnits = BigInteger.Zero;
            return true;
        }

        var ok = AmountConversion.TryToBaseUnits(display, decimals, out var converted);
        baseUnits = ok ? converted : BigInteger.Zero;
        return ok;
    }
}
