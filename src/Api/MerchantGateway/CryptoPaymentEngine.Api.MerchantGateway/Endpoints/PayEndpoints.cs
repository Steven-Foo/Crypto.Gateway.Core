using System.Numerics;
using CryptoPaymentEngine.Api.MerchantGateway.Money;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantGateway.Endpoints;

/// <summary>
/// The hosted pay-page data contract (<c>/pay/{ref}/info</c>) — the JSON the payer's page fetches. The HTML
/// page itself is frontend-owned. Unauthenticated: the reference is an unguessable public id, and it exposes
/// only what a payer needs. The status is the effective one (a lapsed invoice already reads "expired").
/// </summary>
public static class PayEndpoints
{
    public static void MapPayApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/pay/{reference:guid}/info", GetInfoAsync);
    }

    private static async Task<IResult> GetInfoAsync(
        Guid reference, HttpContext http, IPaymentIntentDirectory directory, IAssetCatalog assets)
    {
        var view = await directory.FindByPublicReferenceAsync(reference, http.RequestAborted);
        if (view is null)
            return Results.NotFound();

        // USDT-only pay flow (frozen contract): resolve decimals from the catalog for display conversion.
        var asset = await assets.FindAsync(Chain.Tron, "USDT", http.RequestAborted);
        var decimals = asset?.Decimals ?? 6;

        return Results.Ok(new
        {
            address = view.Address,
            amount = AmountConversion.ToDisplay(BigInteger.Parse(view.ExpectedAmountBaseUnits), decimals),
            expiresAt = view.ExpiresAt,
            status = view.Status,
        });
    }
}
