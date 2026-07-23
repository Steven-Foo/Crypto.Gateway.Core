using CryptoPaymentEngine.Api.MerchantGateway.Models;
using CryptoPaymentEngine.Api.MerchantGateway.Money;
using CryptoPaymentEngine.Api.MerchantGateway.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantGateway.Endpoints;

/// <summary>
/// The frozen merchant API, as the anti-corruption edge: authenticate (upstream middleware) → resolve the
/// asset + convert display↔base-units → call a module through its Contract/Application → map the
/// <c>Result</c> to the partner's <c>ApiResponse</c>. No business logic lives here (§4.7).
/// </summary>
public static class MerchantApiEndpoints
{
    private const string TronChainType = "TRC";

    public static void MapMerchantApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1");
        group.MapPost("/deposit", DepositAsync);
        group.MapPost("/withdraw", WithdrawAsync);
        group.MapPost("/balance", BalanceAsync);
    }

    private static async Task<IResult> DepositAsync(
        DepositRequest request, HttpContext http, IAssetCatalog assets, IPaymentIntentService intents, IConfiguration configuration)
    {
        var asset = await assets.FindAsync(Chain.Tron, SymbolFor(request.PaymentMethod), http.RequestAborted);
        if (asset is null)
            return Fail(StatusCodes.Status400BadRequest, "Unsupported payment method.");

        if (!AmountConversion.TryToBaseUnits(request.ExpectedAmount, asset.Decimals, out var amount))
            return Fail(StatusCodes.Status400BadRequest, "Invalid expected amount for this asset's precision.");

        var result = await intents.CreateAsync(
            new CreatePaymentIntentCommand(MerchantId(http), request.TransactionId, asset.Chain, asset.AssetId, amount, request.CallbackUrl),
            http.RequestAborted);
        if (result.IsFailure)
            return Fail(StatusFor(result.Error!), result.Error!.Message);

        var baseUrl = (configuration["Gateway:BaseUrl"] ?? string.Empty).TrimEnd('/');
        return Results.Ok(ApiResponse.Ok(new
        {
            referenceNo = result.Value.Reference,
            address = result.Value.Address,
            chainType = TronChainType,
            createdAt = result.Value.CreatedAt,
            payUrl = $"{baseUrl}/pay/{result.Value.Reference}",
        }));
    }

    private static async Task<IResult> WithdrawAsync(
        WithdrawRequest request, HttpContext http, IAssetCatalog assets, IWithdrawalRequestService withdrawals)
    {
        var asset = await assets.FindAsync(Chain.Tron, SymbolFor(request.PaymentMethod), http.RequestAborted);
        if (asset is null)
            return Fail(StatusCodes.Status400BadRequest, "Unsupported payment method.");

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var amount))
            return Fail(StatusCodes.Status400BadRequest, "Invalid amount for this asset's precision.");

        var result = await withdrawals.RequestAsync(
            new RequestWithdrawalCommand(MerchantId(http), asset.AssetId, asset.Chain, request.ToAddress, amount, request.TransactionId),
            http.RequestAborted);
        if (result.IsFailure)
            return Fail(StatusCodes.Status400BadRequest, result.Error!.Message);

        return Results.Ok(ApiResponse.Ok(new
        {
            referenceNo = result.Value.WithdrawalId,
            txHash = (string?)null,
            amount = request.Amount,
            tokenType = asset.Symbol,
            toAddress = request.ToAddress,
            status = MapWithdrawalStatus(result.Value.Status),
        }));
    }

    private static async Task<IResult> BalanceAsync(HttpContext http, IAssetCatalog assets, ILedgerQuery ledger)
    {
        var asset = await assets.FindAsync(Chain.Tron, "USDT", http.RequestAborted);
        if (asset is null)
            return Fail(StatusCodes.Status500InternalServerError, "USDT asset is not configured.");

        var balance = await ledger.GetMerchantBalanceAsync(MerchantId(http), asset.AssetId, http.RequestAborted);
        return Results.Ok(ApiResponse.Ok(new
        {
            balance = AmountConversion.ToDisplay(balance, asset.Decimals),
            currency = asset.Symbol,
        }));
    }

    private static Guid MerchantId(HttpContext http) => (Guid)http.Items[MerchantSignatureMiddleware.MerchantIdItem]!;

    private static string SymbolFor(string paymentMethod) => paymentMethod.Trim().ToUpperInvariant();

    /// <summary>Maps our richer withdrawal lifecycle onto the partner's frozen 3-value vocabulary.</summary>
    private static string MapWithdrawalStatus(string status) => status switch
    {
        "Confirmed" => "confirmed",
        "Rejected" or "Failed" => "failed",
        _ => "pending",
    };

    private static IResult Fail(int status, string message) => Results.Json(ApiResponse.Fail(message), statusCode: status);

    /// <summary>Duplicate transactionId (a Conflict-typed error) reports 409; everything else stays 400.</summary>
    private static int StatusFor(Error error) => error.Type switch
    {
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };
}
