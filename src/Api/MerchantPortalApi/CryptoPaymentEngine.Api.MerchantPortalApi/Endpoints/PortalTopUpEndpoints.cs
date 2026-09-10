using CryptoPaymentEngine.Api.MerchantPortalApi.Models;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The merchant funding its own balance: this issues a deposit address and the merchant sends crypto to it.
/// It is a real on-chain deposit — the same scanner, confirmation depth, and ledger posting a customer payment
/// gets — so custody genuinely rises and reconciliation stays exact. Nothing here moves money; it only creates
/// the invoice that tells the detection pipeline how to price and classify what arrives.
///
/// <para><b>Pricing is identical to a customer payment</b>: the invoice asks for exactly the amount typed —
/// neither kind is grossed up — and the fee is deducted from what arrives, so the sender is credited
/// amount − fee. The only difference is <em>which</em> fee applies: a top-up uses the merchant's top-up
/// schedule, which defaults to zero and never inherits the platform default deposit fee.</para>
///
/// <para><b>The one deliberate behavioural difference — no T+N hold.</b> The settlement period exists to hold
/// customer money through chargeback and reorg risk; a merchant's own float is not customer money, so a top-up
/// is spendable as soon as it confirms. That exemption is why this is permission-gated separately — see
/// <see cref="PortalPermissions.TopUp"/>.</para>
///
/// <para>Tenant-scoped like every portal endpoint: the merchant id comes only from the validated session, so a
/// merchant can never raise a top-up against another merchant's balance.</para>
/// </summary>
public static class PortalTopUpEndpoints
{
    public static void MapPortalTopUpApi(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/portal/top-ups", CreateAsync).RequirePortalPermission(PortalPermissions.TopUp.Create);

    private static async Task<IResult> CreateAsync(
        CreatePortalTopUpRequest request,
        IPaymentIntentService intents,
        IAssetCatalog assets,
        HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Network, ignoreCase: true, out var chain))
            return Bad(PortalErrorCodes.InvalidChain, $"Unknown network '{request.Network}'.");

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return Bad(PortalErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}.");

        // Display → base units at the edge, refusing over-precision rather than truncating money (§14).
        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var amount))
            return Bad(PortalErrorCodes.InvalidAmount, "amount must be positive and within this asset's precision.");

        var merchantId = PortalTenant.MerchantId(http);

        var result = await intents.CreateAsync(
            new CreatePaymentIntentCommand(
                merchantId,
                request.MerchantOrderNumber,
                chain,
                asset.AssetId,
                amount,
                CallbackUrl: null,
                Kind: PaymentIntentKind.MerchantTopUp),
            http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                reference = result.Value.Reference,
                // Where to send the funds, and exactly how much — no gross-up, so this equals what was asked for.
                address = result.Value.Address,
                network = result.Value.Chain.ToString(),
                coin = asset.Symbol,
                amount = request.Amount,
                amountBaseUnits = amount.ToString(),
                decimals = asset.Decimals,
                expiresAt = result.Value.ExpiresAt,
                createdAt = result.Value.CreatedAt,
            },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }

    /// <summary>A reused merchant reference is a 409 — a resubmitted request must never quietly create a
    /// second invoice on a different address; every other business failure is a 400. Keyed off the error's
    /// <see cref="ErrorType"/> rather than a hardcoded code string, so renaming a code cannot silently
    /// downgrade a conflict to a 400.</summary>
    private static IResult Fail(Error error) =>
        Results.Json(
            new { isSuccess = false, data = (object?)null, error = error.Message, errorCode = error.Code },
            statusCode: error.Type == ErrorType.Conflict
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest);

    private static IResult Bad(string errorCode, string message) => PortalResults.Bad(errorCode, message);
}
