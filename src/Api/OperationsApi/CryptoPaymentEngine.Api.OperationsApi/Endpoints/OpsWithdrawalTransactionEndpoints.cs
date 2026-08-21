using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
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
        app.MapGet("/api/v1/ops/transactions/withdrawals", ListAsync).RequirePermission(OpsPermissions.Withdrawals.View);

    private static async Task<IResult> ListAsync(
        IWithdrawalDirectory withdrawals,
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
        string? kind = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        // An empty-result short-circuit still returns every field the populated path returns — same shape,
        // zeroed — so the frontend never has to special-case a no-match response.
        IResult EmptyPage() => Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                page, pageSize, totalCount = 0, totalTransactionRecords = 0,
                totalWithdrawalAmount = 0m, totalFee = 0m, distinctAssetCount = 0,
                items = Array.Empty<object>(),
            },
            error = (string?)null,
        });

        // Free-text merchant-name search: resolve to ids first (Withdrawal never learns Merchant's schema,
        // §4.5). No match ⇒ short-circuit to an empty page, same pattern as an unknown coin below.
        IReadOnlyList<Guid>? merchantIds = null;
        if (!string.IsNullOrWhiteSpace(merchantName))
        {
            merchantIds = await merchants.SearchIdsByNameAsync(merchantName, http.RequestAborted);
            if (merchantIds.Count == 0)
                return EmptyPage();
        }

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
                return EmptyPage();

            assetId = coinAsset.AssetId;
        }

        var filter = new WithdrawalAdminFilter(
            merchantId, systemOrderNumber, merchantOrderNumber, receivingAddress, network, assetId, fromDate, toDate,
            normalisedKind, MerchantIds: merchantIds);
        var (items, total) = await withdrawals.SearchAsync(filter, page, pageSize, http.RequestAborted);

        // Totals across the WHOLE filtered set (every page, not just this one) — the summary row above the
        // table. totalsDecimals uses the coin filter's precision when one is set (the exact, correct case);
        // otherwise falls back to 6 like every per-row conversion below (§14 — see distinctAssetCount: if the
        // filtered set spans more than one asset, these sums are added together across different-decimal
        // assets and are only approximate, deliberately surfaced rather than silently hidden).
        var totals = await withdrawals.GetTotalsAsync(filter, http.RequestAborted);
        var totalsAsset = assetId is { } fixedAssetId ? await assets.FindByIdAsync(fixedAssetId, http.RequestAborted) : null;
        var totalsDecimals = totalsAsset?.Decimals ?? 6;

        var callbackStatuses = await callbacks.GetStatusesAsync(
            CallbackReferenceType.Withdrawal, items.Select(w => w.WithdrawalId).ToList(), http.RequestAborted);

        var merchantNames = await merchants.GetNamesByIdsAsync(
            items.Select(w => w.MerchantId).Distinct().ToList(), http.RequestAborted);

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
                merchantName = merchantNames.GetValueOrDefault(withdrawal.MerchantId),
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
                txHash = withdrawal.TransactionHash,
                sourceWalletId = withdrawal.SourceWalletId,
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
            data = new
            {
                page,
                pageSize,
                totalCount = total,
                // Summary totals across the whole filtered set, not just this page (§14 — see the comment
                // above on totalsDecimals for the multi-asset caveat).
                totalTransactionRecords = total,
                totalWithdrawalAmount = AmountConversion.ToDisplay(BigInteger.Parse(totals.TotalAmountBaseUnits), totalsDecimals),
                totalFee = AmountConversion.ToDisplay(BigInteger.Parse(totals.TotalFeeBaseUnits), totalsDecimals),
                distinctAssetCount = totals.DistinctAssetCount,
                items = rows,
            },
            error = (string?)null,
        });
    }
}
