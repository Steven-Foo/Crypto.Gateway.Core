using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>The signed-in merchant's declared per-asset pricing (read-only in the portal — staff set it via the
/// Ops API). <b>Standardized on plain percent on the wire</b> (e.g. <c>2</c> = 2%) — the domain/DB stay in
/// basis points, converted here at the edge; nothing upstream of this endpoint should ever see a bps value.</summary>
public static class PortalFeeEndpoints
{
    public static void MapPortalFeeApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/portal/fees", GetAsync).RequirePortalPermission(PortalPermissions.Overview.View);

    private static async Task<IResult> GetAsync(
        IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        var result = await policies.ListAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status404NotFound);

        var rows = new List<object>(result.Value.Count);
        foreach (var p in result.Value)
        {
            var asset = await assets.FindByIdAsync(p.AssetId, http.RequestAborted);
            var decimals = asset?.Decimals ?? 6;

            rows.Add(new
            {
                assetId = p.AssetId,
                coin = asset?.Symbol ?? "",
                network = asset?.Chain.ToString(),
                depositFeeFixed = Display(p.DepositFeeFixed, decimals),
                depositFeePercent = Percent(p.DepositFeeBps),
                withdrawalFeeFixed = Display(p.WithdrawalFee, decimals),
                withdrawalFeePercent = Percent(p.WithdrawalFeeBps),
                // The merchant funding its own balance (POST /portal/top-ups) is priced on its own schedule,
                // which defaults to zero and never inherits the platform default deposit fee. Surfaced here so
                // a merchant can see what a top-up costs before sending — it was previously chargeable but
                // invisible on this screen.
                topUpFeeFixed = Display(p.TopUpFeeFixed, decimals),
                topUpFeePercent = Percent(p.TopUpFeeBps),
                merchantWithdrawalFlatCap = Display(p.MerchantWithdrawalFlatCap, decimals),
                merchantWithdrawalCapPercent = Percent(p.MerchantWithdrawalPercentBps),
                minimumWithdrawal = Display(p.MinimumWithdrawal, decimals),
                maximumWithdrawal = Display(p.MaximumWithdrawal, decimals),
                approvalThreshold = Display(p.ApprovalThreshold, decimals),
            });
        }

        return Results.Ok(new { isSuccess = true, data = new { items = rows }, error = (string?)null, errorCode = (string?)null });
    }

    private static decimal? Display(string? baseUnits, int decimals) =>
        string.IsNullOrEmpty(baseUnits) || !BigInteger.TryParse(baseUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? null
            : AmountConversion.ToDisplay(value, decimals);

    /// <summary>Basis points → plain percent (e.g. <c>200</c> bps → <c>2</c>) — the wire-format conversion; the
    /// domain/DB stay in bps.</summary>
    private static decimal Percent(int bps) => bps / 100m;
}
