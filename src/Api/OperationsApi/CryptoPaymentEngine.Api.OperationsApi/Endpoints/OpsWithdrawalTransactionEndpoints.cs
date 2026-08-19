using System.Numerics;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing withdrawal-transaction search — the frontend's dedicated withdrawal screen. No payer
/// address or received-amount here (see <see cref="OpsDepositTransactionEndpoints"/> for why those are
/// deposit-only).
/// </summary>
public static class OpsWithdrawalTransactionEndpoints
{
    public static void MapOpsWithdrawalTransactionApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/transactions/withdrawals", ListAsync);

    private static async Task<IResult> ListAsync(
        IWithdrawalDirectory withdrawals,
        ICallbackDeliveryQuery callbacks,
        IAssetCatalog assets,
        HttpContext http,
        Guid? merchantId = null,
        Guid? systemOrderNumber = null,
        string? merchantOrderNumber = null,
        string? receivingAddress = null,
        Chain? network = null,
        string? coin = null,
        string? kind = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        // Optional withdrawal-kind filter: "user" (end-user payout) or "merchant" (earnings cash-out).
        string? normalisedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            normalisedKind = kind.Trim().ToLowerInvariant() switch
            {
                "user" => "User",
                "merchant" => "Merchant",
                _ => null,
            };
            if (normalisedKind is null)
                return Results.Json(
                    new { isSuccess = false, error = "kind must be 'user' or 'merchant'." },
                    statusCode: StatusCodes.Status400BadRequest);
        }

        Guid? assetId = null;
        if (!string.IsNullOrWhiteSpace(coin))
        {
            if (network is null)
                return Results.Json(
                    new { isSuccess = false, error = "network is required when filtering by coin." },
                    statusCode: StatusCodes.Status400BadRequest);

            var coinAsset = await assets.FindAsync(network.Value, coin.Trim().ToUpperInvariant(), http.RequestAborted);
            if (coinAsset is null)
                return Results.Ok(new
                {
                    isSuccess = true,
                    data = new { page, pageSize, totalCount = 0, items = Array.Empty<object>() },
                    error = (string?)null,
                });

            assetId = coinAsset.AssetId;
        }

        var filter = new WithdrawalAdminFilter(
            merchantId, systemOrderNumber, merchantOrderNumber, receivingAddress, network, assetId, fromDate, toDate,
            normalisedKind);
        var (items, total) = await withdrawals.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var callbackStatuses = await callbacks.GetStatusesAsync(
            CallbackReferenceType.Withdrawal, items.Select(w => w.WithdrawalId).ToList(), http.RequestAborted);

        var assetCache = new Dictionary<Guid, AssetDto?>();
        var rows = new List<object>(items.Count);
        foreach (var withdrawal in items)
        {
            if (!assetCache.TryGetValue(withdrawal.AssetId, out var asset))
            {
                asset = await assets.FindByIdAsync(withdrawal.AssetId, http.RequestAborted);
                assetCache[withdrawal.AssetId] = asset;
            }

            var decimals = asset?.Decimals ?? 6;
            var callback = callbackStatuses.GetValueOrDefault(withdrawal.WithdrawalId);

            rows.Add(new
            {
                merchantId = withdrawal.MerchantId,
                systemOrderNumber = withdrawal.WithdrawalId,
                merchantOrderNumber = withdrawal.MerchantTransactionId,
                userId = (string?)null, // not implemented yet — always null (§ docs/backoffice-api.md)
                receivingAddress = withdrawal.DestinationAddress,
                network = withdrawal.Chain.ToString(),
                coin = asset?.Symbol ?? "",
                expectedAmount = AmountConversion.ToDisplay(BigInteger.Parse(withdrawal.AmountBaseUnits), decimals),
                // The per-merchant fee the merchant bore, snapshotted on the withdrawal at request (§14).
                fee = AmountConversion.ToDisplay(BigInteger.Parse(withdrawal.FeeBaseUnits), decimals),
                confirms = withdrawal.Confirmations,
                type = "withdrawal",
                // "User" (end-user payout) vs "Merchant" (earnings cash-out) — the two share the pipeline but
                // are distinct money-out kinds; the screen can now filter/label them.
                kind = withdrawal.Kind,
                createdAt = withdrawal.CreatedAt,
                status = withdrawal.Status,
                callback = callback?.Status,
                callbackFailedCount = callback?.AttemptCount ?? 0,
                callbackNextAttemptAt = callback?.NextAttemptAt,
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items = rows },
            error = (string?)null,
        });
    }
}
