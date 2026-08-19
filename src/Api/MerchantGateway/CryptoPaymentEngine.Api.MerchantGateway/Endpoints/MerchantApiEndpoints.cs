using CryptoPaymentEngine.Api.MerchantGateway.Models;
using CryptoPaymentEngine.Api.MerchantGateway.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
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
        group.MapPost("/merchant-withdraw", MerchantWithdrawAsync);
        group.MapPost("/balance", BalanceAsync);
        group.MapPost("/transactions/query", TransactionQueryAsync);
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
            new RequestWithdrawalCommand(
                MerchantId(http), asset.AssetId, asset.Chain, request.ToAddress, amount, request.TransactionId, request.CallbackUrl),
            http.RequestAborted);
        if (result.IsFailure)
        {
            // Only a duplicate transactionId escalates to 409 — a resent request must not create a second
            // payout (mirrors /deposit). Every other withdrawal failure keeps its existing 400 so the frozen
            // partner error contract is unchanged. Code mirrors WithdrawalErrors.DuplicateReference.
            var status = result.Error!.Code == "withdrawal.duplicate_reference"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Fail(status, result.Error!.Message);
        }

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

    /// <summary>
    /// The merchant cashing out its own earnings to its pre-registered settlement wallet (a Merchant
    /// Withdrawal). No destination is accepted — it is resolved server-side (§10). Gated by the flat/%
    /// liquidity cap, not the user min/max; charges the same withdrawal fee. Duplicate reference ⇒ 409.
    /// </summary>
    private static async Task<IResult> MerchantWithdrawAsync(
        MerchantWithdrawRequest request, HttpContext http, IAssetCatalog assets, IMerchantWithdrawalService merchantWithdrawals)
    {
        var asset = await assets.FindAsync(Chain.Tron, SymbolFor(request.PaymentMethod), http.RequestAborted);
        if (asset is null)
            return Fail(StatusCodes.Status400BadRequest, "Unsupported payment method.");

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var amount))
            return Fail(StatusCodes.Status400BadRequest, "Invalid amount for this asset's precision.");

        var result = await merchantWithdrawals.RequestAsync(
            new MerchantWithdrawalCommand(MerchantId(http), asset.AssetId, asset.Chain, amount, request.TransactionId, request.CallbackUrl),
            http.RequestAborted);
        if (result.IsFailure)
        {
            var status = result.Error!.Code == "withdrawal.duplicate_reference"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Fail(status, result.Error!.Message);
        }

        return Results.Ok(ApiResponse.Ok(new
        {
            referenceNo = result.Value.WithdrawalId,
            txHash = (string?)null,
            amount = request.Amount,
            tokenType = asset.Symbol,
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

    /// <summary>
    /// Looks up one of the merchant's own transactions by its <c>transactionId</c>, scoped to the calling
    /// merchant (never a request parameter, §4.5/§7.3). With no <c>kind</c> it auto-detects in order:
    /// deposit (PaymentIntent) → user payout → merchant cash-out. An explicit <c>kind</c> ("user" | "merchant")
    /// narrows the withdrawal lookup — needed because a user payout and a merchant cash-out can share one
    /// reference (the idempotency key is <c>(merchant, kind, reference)</c>). User payouts keep the frozen
    /// <c>type = "withdraw"</c> shape; cash-outs return the additive <c>type = "merchant_withdraw"</c>.
    /// </summary>
    private static async Task<IResult> TransactionQueryAsync(
        TransactionQueryRequest request, HttpContext http, IAssetCatalog assets,
        IPaymentIntentDirectory intents, IWithdrawalDirectory withdrawals)
    {
        var merchantId = MerchantId(http);

        var kind = request.Kind?.Trim();
        var isUser = kind is null || kind.Equals("user", StringComparison.OrdinalIgnoreCase);
        var isMerchant = kind is null || kind.Equals("merchant", StringComparison.OrdinalIgnoreCase);
        if (kind is not null && !isUser && !isMerchant)
            return Fail(StatusCodes.Status400BadRequest, "kind must be 'user' or 'merchant' when provided.");

        // Deposit has no kind — only auto-detected when the caller didn't ask for a specific withdrawal kind.
        if (kind is null)
        {
            var deposit = await intents.FindByMerchantReferenceAsync(merchantId, request.TransactionId, http.RequestAborted);
            if (deposit is not null)
            {
                var depositAsset = await assets.FindByIdAsync(deposit.AssetId, http.RequestAborted);
                return Results.Ok(ApiResponse.Ok(new
                {
                    type = "deposit",
                    referenceNo = deposit.PublicReference,
                    status = deposit.Status,
                    amount = AmountConversion.ToDisplay(BigInteger.Parse(deposit.ExpectedAmountBaseUnits), depositAsset?.Decimals ?? 6),
                    currency = depositAsset?.Symbol ?? "",
                    address = deposit.Address,
                    expiresAt = deposit.ExpiresAt,
                }));
            }
        }

        if (isUser)
        {
            var payout = await withdrawals.FindByMerchantReferenceAsync(merchantId, request.TransactionId, "User", http.RequestAborted);
            if (payout is not null)
                return await WithdrawalResultAsync(payout, assets, "withdraw", http.RequestAborted);
        }

        if (isMerchant)
        {
            var cashOut = await withdrawals.FindByMerchantReferenceAsync(merchantId, request.TransactionId, "Merchant", http.RequestAborted);
            if (cashOut is not null)
                return await WithdrawalResultAsync(cashOut, assets, "merchant_withdraw", http.RequestAborted);
        }

        return Fail(StatusCodes.Status404NotFound, "No deposit or withdrawal found for this transactionId.");
    }

    /// <summary>Shapes a withdrawal-side query hit. <paramref name="type"/> is "withdraw" for a user payout
    /// (the frozen shape) or "merchant_withdraw" for a cash-out — the only difference between the two.</summary>
    private static async Task<IResult> WithdrawalResultAsync(
        WithdrawalView withdrawal, IAssetCatalog assets, string type, CancellationToken cancellationToken)
    {
        var asset = await assets.FindByIdAsync(withdrawal.AssetId, cancellationToken);
        return Results.Ok(ApiResponse.Ok(new
        {
            type,
            referenceNo = withdrawal.WithdrawalId,
            status = MapWithdrawalStatus(withdrawal.Status),
            amount = AmountConversion.ToDisplay(BigInteger.Parse(withdrawal.AmountBaseUnits), asset?.Decimals ?? 6),
            currency = asset?.Symbol ?? "",
            toAddress = withdrawal.DestinationAddress,
            txHash = withdrawal.TransactionHash,
            createdAt = withdrawal.CreatedAt,
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
