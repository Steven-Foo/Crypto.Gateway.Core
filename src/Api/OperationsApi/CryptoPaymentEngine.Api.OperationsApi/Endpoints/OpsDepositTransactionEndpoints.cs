using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing deposit-transaction search — the frontend's dedicated deposit screen. Deliberately a
/// separate endpoint from the withdrawal one (not one shared "type" query): deposits and withdrawals surface
/// different fields (payer/received-amount only make sense for a deposit), so a shared shape would force
/// nulls one side never populates.
/// </summary>
public static class OpsDepositTransactionEndpoints
{
    public static void MapOpsDepositTransactionApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/transactions/deposits", ListAsync).RequirePermission(OpsPermissions.Deposits.View);

    private static async Task<IResult> ListAsync(
        IPaymentIntentDirectory paymentIntents,
        IDepositLookup deposits,
        ICallbackDeliveryQuery callbacks,
        IAssetCatalog assets,
        IMerchantDirectory merchants,
        HttpContext http,
        Guid? merchantId = null,
        string? merchantName = null,
        Guid? systemOrderNumber = null,
        string? merchantOrderNumber = null,
        string? receivingAddress = null,
        Chain? network = null,
        string? coin = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        // Free-text merchant-name search: resolve to ids first (Deposit/PaymentIntent never learn Merchant's
        // schema, §4.5). No match ⇒ short-circuit to an empty page, same pattern as an unknown coin below.
        IReadOnlyList<Guid>? merchantIds = null;
        if (!string.IsNullOrWhiteSpace(merchantName))
        {
            merchantIds = await merchants.SearchIdsByNameAsync(merchantName, http.RequestAborted);
            if (merchantIds.Count == 0)
                return Results.Ok(new
                {
                    isSuccess = true,
                    data = new { page, pageSize, totalCount = 0, items = Array.Empty<object>() },
                    error = (string?)null,
                });
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

        var filter = new PaymentIntentAdminFilter(
            merchantId, systemOrderNumber, merchantOrderNumber, receivingAddress, network, assetId, fromDate, toDate,
            MerchantIds: merchantIds);
        var (items, total) = await paymentIntents.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var matchedDepositIds = items.Where(i => i.MatchedDepositId is not null).Select(i => i.MatchedDepositId!.Value).ToList();
        var depositSummaries = await deposits.GetByIdsAsync(matchedDepositIds, http.RequestAborted);

        var callbackStatuses = await callbacks.GetStatusesAsync(
            CallbackReferenceType.Deposit, items.Select(i => i.PublicReference).ToList(), http.RequestAborted);

        var merchantNames = await merchants.GetNamesByIdsAsync(
            items.Select(i => i.MerchantId).Distinct().ToList(), http.RequestAborted);

        var assetCache = new Dictionary<Guid, AssetDto?>();
        var rows = new List<object>(items.Count);
        foreach (var intent in items)
        {
            if (!assetCache.TryGetValue(intent.AssetId, out var asset))
            {
                asset = await assets.FindByIdAsync(intent.AssetId, http.RequestAborted);
                assetCache[intent.AssetId] = asset;
            }

            var decimals = asset?.Decimals ?? 6;
            var matched = intent.MatchedDepositId is { } depositId ? depositSummaries.GetValueOrDefault(depositId) : null;
            var callback = callbackStatuses.GetValueOrDefault(intent.PublicReference);

            rows.Add(new
            {
                merchantId = intent.MerchantId,
                merchantName = merchantNames.GetValueOrDefault(intent.MerchantId),
                systemOrderNumber = intent.PublicReference,
                merchantOrderNumber = intent.MerchantTransactionId,
                userId = (string?)null,           // not implemented yet — always null (§ docs/backoffice-api.md)
                payerAddress = (string?)null,     // not captured on-chain yet — always null (§ docs/backoffice-api.md)
                receivingAddress = intent.Address,
                network = intent.Chain.ToString(),
                coin = asset?.Symbol ?? "",
                expectedAmount = AmountConversion.ToDisplay(BigInteger.Parse(intent.ExpectedAmountBaseUnits), decimals),
                receivedAmount = matched is null ? (decimal?)null : AmountConversion.ToDisplay(BigInteger.Parse(matched.AmountBaseUnits), decimals),
                txHash = matched?.TransactionHash,
                // The platform fee actually charged, snapshotted on the matched deposit at detection (§14). Null
                // until a deposit matches this invoice — no deposit, no fee charged yet.
                fee = matched is null ? (decimal?)null : AmountConversion.ToDisplay(BigInteger.Parse(matched.FeeBaseUnits), decimals),
                confirms = matched?.Confirmations,
                type = "deposit",
                createdAt = intent.CreatedAt,
                status = intent.Status,
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
