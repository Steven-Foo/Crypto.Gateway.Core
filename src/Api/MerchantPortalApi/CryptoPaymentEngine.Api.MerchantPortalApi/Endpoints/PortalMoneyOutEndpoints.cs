using CryptoPaymentEngine.Api.MerchantPortalApi.Models;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The portal's two money-out actions. Both call the SAME Application services the HMAC-signed
/// <c>MerchantGateway</c> endpoints use — the service layer is the money boundary, not the host — so every
/// existing control still applies unchanged: idempotency on the merchant's reference, the per-merchant fee, the
/// settled-balance (T+N) gate, the liquidity cap, the ledger reserve as the atomic overdraw guard, and the
/// per-merchant approval threshold above which platform staff must approve.
///
/// <para><b>Risk note on payouts.</b> A payout sends to an address supplied in the request, so exposing it to a
/// browser session widens the blast radius of a stolen session (XSS/cookie theft) compared with the
/// server-to-server HMAC path, where the signing secret never leaves the merchant's server. That trade-off was
/// made deliberately; it is contained by giving payouts their own permission code (off unless a merchant admin
/// grants it) and by the approval threshold, which still forces staff review above the configured amount. A
/// cash-out carries no such risk — its destination is the staff-whitelisted settlement wallet (§10).</para>
/// </summary>
public static class PortalMoneyOutEndpoints
{
    public static void MapPortalMoneyOutApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/portal/payouts", CreatePayoutAsync).RequirePortalPermission(PortalPermissions.Payouts.Create);
        app.MapPost("/api/v1/portal/cash-outs", CreateCashOutAsync).RequirePortalPermission(PortalPermissions.CashOut.Create);

        // The merchant-side approval stage. A separate permission from Create, so a user who may only submit a
        // payout cannot also sign it off.
        app.MapPost("/api/v1/portal/payouts/{id:guid}/approve", ApprovePayoutAsync).RequirePortalPermission(PortalPermissions.Payouts.Approve);
        app.MapPost("/api/v1/portal/payouts/{id:guid}/reject", RejectPayoutAsync).RequirePortalPermission(PortalPermissions.Payouts.Approve);
    }

    /// <summary>
    /// The merchant's approver signs off a payout one of their users submitted. What happens next is the
    /// platform's decision, not theirs: at or below the approval threshold it is cleared to send automatically;
    /// above it, it moves to <c>PendingApproval</c> and waits for platform staff (§10).
    /// </summary>
    private static async Task<IResult> ApprovePayoutAsync(
        Guid id, IMerchantPayoutApprovalService approvals, HttpContext http)
    {
        var principal = PortalTenant.Principal(http);
        var result = await approvals.ApproveAsync(principal.MerchantId, id, principal.Username, http.RequestAborted);

        return result.IsFailure
            ? FailApproval(result.Error!)
            : Ok(new
            {
                systemOrderNumber = result.Value.WithdrawalId,
                status = result.Value.Status,
                // True when the platform still has to approve it — the UI should say "sent for platform approval"
                // rather than implying the payout is on its way.
                awaitingPlatformApproval = result.Value.Status == "PendingApproval",
            });
    }

    private static async Task<IResult> RejectPayoutAsync(
        Guid id, RejectPortalPayoutRequest request, IMerchantPayoutApprovalService approvals, HttpContext http)
    {
        var principal = PortalTenant.Principal(http);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Rejected by merchant approver." : request.Reason.Trim();

        var result = await approvals.RejectAsync(principal.MerchantId, id, principal.Username, reason, http.RequestAborted);
        return result.IsFailure
            ? FailApproval(result.Error!)
            : Ok(new { systemOrderNumber = result.Value.WithdrawalId, status = result.Value.Status });
    }

    /// <summary>A withdrawal belonging to another tenant, or one no longer awaiting merchant approval, is a 404
    /// / 409 respectively — never a silent no-op.</summary>
    private static IResult FailApproval(Error error) =>
        Results.Json(
            new { isSuccess = false, error = error.Message },
            statusCode: error.Type switch
            {
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            });

    private static async Task<IResult> CreatePayoutAsync(
        CreatePortalPayoutRequest request, IAssetCatalog assets, IWithdrawalRequestService withdrawals, HttpContext http)
    {
        var (asset, amount, error) = await ResolveAsync(assets, request.Network, request.Coin, request.Amount, http);
        if (error is not null)
            return error;

        if (string.IsNullOrWhiteSpace(request.ReceivingAddress))
            return Bad("receivingAddress is required.");

        // RequiresMerchantApproval: a human submitted this in the portal, so it waits for the merchant's OWN
        // approver before the platform evaluates it (the two-party rule). The HMAC API passes false.
        var result = await withdrawals.RequestAsync(
            new RequestWithdrawalCommand(
                PortalTenant.MerchantId(http), asset!.AssetId, asset.Chain, request.ReceivingAddress.Trim(), amount,
                request.MerchantOrderNumber.Trim(), CallbackUrl: null, RequiresMerchantApproval: true),
            http.RequestAborted);

        return result.IsFailure
            ? FailWithdrawal(result.Error!)
            : Ok(new
            {
                systemOrderNumber = result.Value.WithdrawalId,
                merchantOrderNumber = request.MerchantOrderNumber.Trim(),
                network = asset.Chain.ToString(),
                coin = asset.Symbol,
                amount = request.Amount,
                receivingAddress = request.ReceivingAddress.Trim(),
                status = result.Value.Status,
            });
    }

    private static async Task<IResult> CreateCashOutAsync(
        CreatePortalCashOutRequest request, IAssetCatalog assets, IMerchantWithdrawalService cashOuts, HttpContext http)
    {
        var (asset, amount, error) = await ResolveAsync(assets, request.Network, request.Coin, request.Amount, http);
        if (error is not null)
            return error;

        // No destination is accepted — MerchantWithdrawalService resolves the whitelisted settlement wallet (§10).
        var result = await cashOuts.RequestAsync(
            new MerchantWithdrawalCommand(
                PortalTenant.MerchantId(http), asset!.AssetId, asset.Chain, amount, request.MerchantOrderNumber.Trim()),
            http.RequestAborted);

        return result.IsFailure
            ? FailWithdrawal(result.Error!)
            : Ok(new
            {
                systemOrderNumber = result.Value.WithdrawalId,
                merchantOrderNumber = request.MerchantOrderNumber.Trim(),
                network = asset.Chain.ToString(),
                coin = asset.Symbol,
                amount = request.Amount,
                status = result.Value.Status,
            });
    }

    /// <summary>Resolves the asset and converts the display amount to base units at the edge, refusing
    /// over-precision rather than truncating money (§14).</summary>
    private static async Task<(AssetDto? Asset, System.Numerics.BigInteger Amount, IResult? Error)> ResolveAsync(
        IAssetCatalog assets, string network, string coin, decimal displayAmount, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(network, ignoreCase: true, out var chain))
            return (null, default, Bad($"Unknown network '{network}'."));

        var asset = await assets.FindAsync(chain, coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return (null, default, Bad($"Unknown coin '{coin}' on {chain}."));

        if (!AmountConversion.TryToBaseUnits(displayAmount, asset.Decimals, out var amount))
            return (null, default, Bad("amount must be positive and within this asset's precision."));

        return (asset, amount, null);
    }

    /// <summary>A duplicate merchant reference is a 409 (a resubmitted request must never create a second
    /// payout); every other business failure is a 400 — mirroring the MerchantGateway contract.</summary>
    private static IResult FailWithdrawal(Error error) =>
        Results.Json(
            new { isSuccess = false, error = error.Message },
            statusCode: error.Code == "withdrawal.duplicate_reference"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest);

    private static IResult Ok(object data) =>
        Results.Ok(new { isSuccess = true, data, error = (string?)null });

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
