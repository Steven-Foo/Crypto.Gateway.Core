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
    public static void MapOpsWithdrawalTransactionApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/transactions/withdrawals", ListAsync).RequirePermission(OpsPermissions.Withdrawals.View);
        app.MapGet("/api/v1/ops/transactions/withdrawals/{systemOrderNumber:guid}", GetAsync).RequirePermission(OpsPermissions.Withdrawals.View);
    }

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
        string? status = null,
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
        IResult EmptyPage() => OpsResults.Ok(new
        {
            page, pageSize, totalCount = 0, totalTransactionRecords = 0,
            totalWithdrawalAmount = 0m, totalFee = 0m, distinctAssetCount = 0,
            // Exact base-unit counterparts — see the note on the populated path below.
            totalWithdrawalAmountBaseUnits = "0", totalFeeBaseUnits = "0", totalsDecimals = 0,
            items = Array.Empty<object>(),
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
                return OpsResults.Bad(OpsErrorCodes.InvalidWithdrawalKind, "kind must be 'user' or 'merchant'.");
        }

        // Effective-status filter (the same vocabulary the rows below report). Validated here so an unknown
        // value is a 400 rather than an empty queue an operator would read as "no work outstanding".
        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = status.Trim().ToLowerInvariant();
            if (!WithdrawalEffectiveStatuses.IsKnown(normalisedStatus))
                return OpsResults.Bad(OpsErrorCodes.InvalidStatus, $"Unknown status '{status}'. Expected one of: {string.Join(", ", WithdrawalEffectiveStatuses.All)}.");
        }

        Guid? assetId = null;
        if (!string.IsNullOrWhiteSpace(coin))
        {
            if (network is null)
                return OpsResults.Bad(OpsErrorCodes.NetworkRequired, "network is required when filtering by coin.");

            var coinAsset = await assets.FindAsync(network.Value, coin.Trim().ToUpperInvariant(), http.RequestAborted);
            if (coinAsset is null)
                return EmptyPage();

            assetId = coinAsset.AssetId;
        }

        var filter = new WithdrawalAdminFilter(
            merchantId, systemOrderNumber, merchantOrderNumber, receivingAddress, network, assetId, fromDate, toDate,
            normalisedKind, MerchantIds: merchantIds, Status: normalisedStatus);
        var (items, total) = await withdrawals.SearchAsync(filter, page, pageSize, http.RequestAborted);

        // Totals across the WHOLE filtered set (every page, not just this one) — the summary row above the
        // table. totalsDecimals uses the coin filter's precision when one is set (the exact, correct case);
        // otherwise falls back to 6 like every per-row conversion below (§14 — see distinctAssetCount: if the
        // filtered set spans more than one asset, these sums are added together across different-decimal
        // assets and are only approximate, deliberately surfaced rather than silently hidden).
        var totals = await withdrawals.GetTotalsAsync(filter, http.RequestAborted);
        var totalsAsset = assetId is { } fixedAssetId ? await assets.FindByIdAsync(fixedAssetId, http.RequestAborted) : null;
        var totalsDecimals = totalsAsset?.Decimals ?? 6;

        var rows = await BuildRowsAsync(items, assets, merchants, callbacks, http);

        return OpsResults.Ok(new
        {
            page,
            pageSize,
            totalCount = total,
            // Summary totals across the whole filtered set, not just this page (§14 — see the comment
            // above on totalsDecimals for the multi-asset caveat).
            totalTransactionRecords = total,
            totalWithdrawalAmount = AmountConversion.ToDisplay(BigInteger.Parse(totals.TotalAmountBaseUnits), totalsDecimals),
            totalFee = AmountConversion.ToDisplay(BigInteger.Parse(totals.TotalFeeBaseUnits), totalsDecimals),
            // The exact sums. These matter more than the per-row values do: a sum reaches a large
            // magnitude first, so it is the first place a double would start dropping digits.
            totalWithdrawalAmountBaseUnits = totals.TotalAmountBaseUnits,
            totalFeeBaseUnits = totals.TotalFeeBaseUnits,
            // The precision the two decimals above were converted at — 6 unless a coin filter
            // pinned it, which is the same caveat distinctAssetCount surfaces.
            totalsDecimals,
            distinctAssetCount = totals.DistinctAssetCount,
            items = rows,
        });
    }

    /// <summary>
    /// One withdrawal by its system order number (REQ-6) — the deep-linkable detail view. Returns the
    /// <b>identical row shape</b> the list returns, because it is built by the very same projection: a
    /// separately written detail projection is how a field ends up formatted one way on the table and another
    /// way on the record it opens.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid systemOrderNumber,
        IWithdrawalDirectory withdrawals,
        ICallbackDeliveryQuery callbacks,
        IAssetCatalog assets,
        IMerchantDirectory merchants,
        HttpContext http)
    {
        // Reuses the search filter rather than adding a by-id Contract method: SystemOrderNumber is already a
        // unique narrowing, so this returns exactly zero or one row.
        var filter = new WithdrawalAdminFilter(
            null, systemOrderNumber, null, null, null, null, null, null);
        var (items, _) = await withdrawals.SearchAsync(filter, 1, 1, http.RequestAborted);

        if (items.Count == 0)
            return OpsResults.NotFound(OpsErrorCodes.NotFound, $"No withdrawal found with systemOrderNumber '{systemOrderNumber}'.");

        var rows = await BuildRowsAsync(items, assets, merchants, callbacks, http);
        return OpsResults.Ok(rows[0]);
    }

    /// <summary>
    /// The single withdrawal-row projection, shared by the list and the detail endpoint so the two can never
    /// disagree about a field's name, precision, or formatting.
    /// </summary>
    private static async Task<List<object>> BuildRowsAsync(
        IReadOnlyList<WithdrawalAdminRow> items,
        IAssetCatalog assets,
        IMerchantDirectory merchants,
        ICallbackDeliveryQuery callbacks,
        HttpContext http)
    {
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
                receivingAddress = withdrawal.DestinationAddress,
                network = withdrawal.Chain.ToString(),
                coin = asset?.Symbol ?? "",
                expectedAmount = AmountConversion.ToDisplay(BigInteger.Parse(withdrawal.AmountBaseUnits), decimals),
                // The exact value, alongside the display decimal (§14). System.Text.Json writes a
                // `decimal` as a JSON number, and a JavaScript number is a double — so a browser has
                // already lost precision by the time JSON.parse returns, and no client-side care can
                // recover it. These carry the domain's own BigInteger representation, unconverted.
                expectedAmountBaseUnits = withdrawal.AmountBaseUnits,
                // The per-merchant fee the merchant bore, snapshotted on the withdrawal at request (§14).
                fee = AmountConversion.ToDisplay(BigInteger.Parse(withdrawal.FeeBaseUnits), decimals),
                feeBaseUnits = withdrawal.FeeBaseUnits,
                // The asset's precision, so a consumer can format the base units above without a
                // second lookup against the asset catalog.
                decimals,
                confirms = withdrawal.Confirmations,
                txHash = withdrawal.TransactionHash,
                sourceWalletId = withdrawal.SourceWalletId,
                type = "withdrawal",
                // "User" (end-user payout) vs "Merchant" (earnings cash-out) — the two share the pipeline but
                // are distinct money-out kinds; the screen can now filter/label them.
                kind = withdrawal.Kind,
                createdAt = withdrawal.CreatedAt,
                status = withdrawal.Status,
                // Merchant settlements are paid off-system by an admin, so WHO acted and which company wallet
                // paid is the only audit trail there is — the chain cannot tell us, because the source is not
                // ours. All null for a user payout, which the platform pays itself.
                auditedBy = withdrawal.AuditedBy,
                auditedAt = withdrawal.AuditedAt,
                settledBy = withdrawal.CompletedBy,
                settledAt = withdrawal.CompletedAt,
                settlementSourceAddress = withdrawal.SettlementSourceAddress,
                callback = callback?.Status,
                callbackFailedCount = callback?.AttemptCount ?? 0,
                callbackNextAttemptAt = callback?.NextAttemptAt,
            });
        }

        return rows;
    }
}
