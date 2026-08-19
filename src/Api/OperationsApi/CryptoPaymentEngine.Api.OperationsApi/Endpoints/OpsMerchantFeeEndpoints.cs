using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing per-merchant fee configuration — the write path a merchant's <c>flat + %</c> deposit and
/// withdrawal pricing was missing (until now every merchant was unpriced ⇒ zero fee). Amounts cross
/// display↔base-unit only here (§14); the fee math and all bounds live in the domain <c>FeeSchedule</c>
/// behind <see cref="IMerchantAssetPolicyService"/>. Setting a fee changes no money already moved — it prices
/// the merchant's <em>future</em> deposits and withdrawals.
/// </summary>
public static class OpsMerchantFeeEndpoints
{
    public static void MapOpsMerchantFeeApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/merchants/{id:guid}/fees", ListAsync).RequirePermission(OpsPermissions.Fees.View);
        app.MapPut("/api/v1/ops/merchants/{id:guid}/fees", SetAsync).RequirePermission(OpsPermissions.Fees.Manage);
    }

    private static async Task<IResult> ListAsync(
        Guid id, IMerchantAssetPolicyService policies, IAssetCatalog assets, HttpContext http)
    {
        var result = await policies.ListAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var rows = new List<object>(result.Value.Count);
        foreach (var p in result.Value)
        {
            var asset = await assets.FindByIdAsync(p.AssetId, http.RequestAborted);
            var decimals = asset?.Decimals ?? 6;
            rows.Add(new
            {
                assetId = p.AssetId,
                network = asset?.Chain.ToString(),
                coin = asset?.Symbol,
                depositFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.DepositFeeFixed), decimals),
                depositFeeBps = p.DepositFeeBps,
                depositFeePercent = p.DepositFeeBps / 100m,
                withdrawalFeeFixed = AmountConversion.ToDisplay(BigInteger.Parse(p.WithdrawalFee), decimals),
                withdrawalFeeBps = p.WithdrawalFeeBps,
                withdrawalFeePercent = p.WithdrawalFeeBps / 100m,
            });
        }

        return Results.Ok(new { isSuccess = true, data = new { merchantId = id, fees = rows }, error = (string?)null });
    }

    private static async Task<IResult> SetAsync(
        Guid id, SetMerchantFeeRequest request, IMerchantAssetPolicyService policies, IAssetCatalog assets,
        IAuditLogger audit, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return Bad($"Unknown chain '{request.Chain}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad($"Unknown coin '{request.Coin}' on {chain}.");

        if (!TryFeeToBase(request.DepositFeeFixed, asset.Decimals, out var depositFixed))
            return Bad("depositFeeFixed is negative or finer than the asset's precision.");
        if (!TryFeeToBase(request.WithdrawalFeeFixed, asset.Decimals, out var withdrawalFixed))
            return Bad("withdrawalFeeFixed is negative or finer than the asset's precision.");

        var result = await policies.SetFeesAsync(
            id, asset.AssetId, depositFixed, request.DepositFeeBps, withdrawalFixed, request.WithdrawalFeeBps,
            http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.fee_updated", "Merchant", id.ToString(),
            $"{asset.Symbol}: deposit={request.DepositFeeFixed}+{request.DepositFeeBps}bps, withdrawal={request.WithdrawalFeeFixed}+{request.WithdrawalFeeBps}bps",
            actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = chain.ToString() },
            error = (string?)null,
        });
    }

    /// <summary>The fee fixed component: like <c>AmountConversion.TryToBaseUnits</c> but a zero is valid
    /// (a pure-percentage fee). Still refuses negatives and over-precision — never truncates money (§14).</summary>
    private static bool TryFeeToBase(decimal display, int decimals, out BigInteger baseUnits)
    {
        if (display == 0m)
        {
            baseUnits = BigInteger.Zero;
            return true;
        }

        return AmountConversion.TryToBaseUnits(display, decimals, out baseUnits);
    }

    private static IResult Fail(Error error)
    {
        var status = error.Type == ErrorType.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest;
        return Results.Json(new { isSuccess = false, error = error.Message }, statusCode: status);
    }

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
