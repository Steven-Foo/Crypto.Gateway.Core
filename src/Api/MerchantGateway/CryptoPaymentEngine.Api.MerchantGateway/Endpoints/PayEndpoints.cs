using System.Numerics;
using CryptoPaymentEngine.Api.MerchantGateway.Money;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantGateway.Endpoints;

/// <summary>
/// The hosted pay page: <c>/pay/{ref}</c> serves the static HTML shell (QR + countdown, polls <c>/info</c>);
/// <c>/pay/{ref}/info</c> is the JSON data contract it fetches. Both unauthenticated — the reference is an
/// unguessable public id, and only what a payer needs is exposed. Status is the effective one (a
/// lapsed-but-not-yet-swept invoice already reads "expired").
/// </summary>
public static class PayEndpoints
{
    public static void MapPayApi(this IEndpointRouteBuilder app)
    {
        // The hosted pay page (reused as-is from the proven PoC): reads the reference from its own URL,
        // polls /info, renders amount + QR + countdown, flips to "confirmed" on detection. The same static
        // file backs every reference. Unauthenticated — the reference is the unguessable public id.
        app.MapGet("/pay/{reference:guid}", (Guid reference, IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.ContentRootPath, "wwwroot", "pay.html"), "text/html"))
            .ExcludeFromDescription(); // it's a page, not an API operation — keep it out of Swagger

        app.MapGet("/pay/{reference:guid}/info", GetInfoAsync)
            .WithName("GetPayInfo")
            .WithTags("Pay");
    }

    private static IResult GetPageAsync(Guid reference, HttpContext http, IWebHostEnvironment env)
    {
        AddSecurityHeaders(http);
        var path = Path.Combine(env.WebRootPath, "pay.html");
        return Results.File(path, "text/html");
    }

    private static async Task<IResult> GetInfoAsync(
        Guid reference, HttpContext http, IPaymentIntentDirectory directory, IAssetCatalog assets, IDepositLookup deposits)
    {
        AddSecurityHeaders(http);

        var view = await directory.FindByPublicReferenceAsync(reference, http.RequestAborted);
        if (view is null)
            return Results.NotFound();

        // Resolve the invoice's OWN asset — not a hardcoded one. A previous version of this endpoint always
        // read USDT's decimals regardless of which asset the intent was actually for, which would have shown
        // the wrong display amount for anything else (e.g. divided an 18-decimal asset by 10^6).
        var asset = await assets.FindByIdAsync(view.AssetId, http.RequestAborted);
        var decimals = asset?.Decimals ?? 6;

        // UX-only: while still "pending", check whether the scanner has already seen a transfer at this
        // address (Deposit's own Detected status) so the payer isn't staring at a blank spinner for the
        // ~60-90s a real confirmation threshold takes. Never influences the actual credit — that still
        // waits for the full DepositConfirmed event (§9).
        var status = view.Status;
        if (status == "pending" && asset is not null &&
            await deposits.HasDetectedDepositAsync(asset.Chain, view.Address, http.RequestAborted))
        {
            status = "confirming";
        }

        return Results.Ok(new
        {
            address = view.Address,
            amount = AmountConversion.ToDisplay(BigInteger.Parse(view.ExpectedAmountBaseUnits), decimals),
            expiresAt = view.ExpiresAt,
            status,
            symbol = asset?.Symbol ?? "",
            chain = asset?.Chain.ToString() ?? "",
        });
    }

    private static void AddSecurityHeaders(HttpContext http)
    {
        http.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
            "style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self'; " +
            "img-src 'self' data:; " +
            "object-src 'none'; " +
            "base-uri 'self'; " +
            "frame-ancestors 'none';";
        http.Response.Headers["X-Frame-Options"] = "DENY";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        http.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    }
}
