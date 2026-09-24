using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// The default deposit/withdrawal fee template a staff member edits, per asset — pure storage read back by
/// the create-merchant screen to pre-fill its fee inputs. Setting a value here does not touch any merchant's
/// own pricing (that stays <c>POST/PUT .../fees</c>) and does not change how an already-unpriced merchant's
/// fee is resolved at charge time (that stays <c>Merchant:DefaultFee</c>, untouched) — this is UI convenience
/// only, nothing on the money path reads it.
/// </summary>
public static class OpsDefaultFeeEndpoints
{
    public static void MapOpsDefaultFeeApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/merchants/default-fees", ListAsync).RequirePermission(OpsPermissions.Fees.View);
        app.MapPut("/api/v1/ops/merchants/default-fees", SetAsync).RequirePermission(OpsPermissions.Fees.Manage);
    }

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
            var decimals = asset?.Decimals ?? 6;
            rows.Add(new
            {
                chain = asset?.Chain.ToString(),
                coin = asset?.Symbol,
                depositFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.DepositFeeFixed), decimals),
                depositFeePercent = OpsPercent.ToPercent(p.DepositFeeBps),
                depositFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(p.MinimumDepositFee), decimals),
                withdrawalFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.WithdrawalFee), decimals),
                withdrawalFeePercent = OpsPercent.ToPercent(p.WithdrawalFeeBps),
                withdrawalFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(p.MinimumWithdrawalFee), decimals),
            });
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

        var saved = result.Value;
        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                chain = chain.ToString(),
                coin = asset.Symbol,
                depositFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(saved.DepositFeeFixed), asset.Decimals),
                depositFeePercent = OpsPercent.ToPercent(saved.DepositFeeBps),
                depositFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(saved.MinimumDepositFee), asset.Decimals),
                withdrawalFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(saved.WithdrawalFee), asset.Decimals),
                withdrawalFeePercent = OpsPercent.ToPercent(saved.WithdrawalFeeBps),
                withdrawalFeeMinimum = AmountConversion.ToDisplay(BigInteger.Parse(saved.MinimumWithdrawalFee), asset.Decimals),
            },
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
