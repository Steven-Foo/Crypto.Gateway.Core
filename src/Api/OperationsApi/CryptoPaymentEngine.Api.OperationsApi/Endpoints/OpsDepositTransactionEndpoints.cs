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
    public static void MapOpsDepositTransactionApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/transactions/deposits", ListAsync).RequirePermission(OpsPermissions.Deposits.View);
        app.MapGet("/api/v1/ops/transactions/deposits/{systemOrderNumber:guid}", GetAsync).RequirePermission(OpsPermissions.Deposits.View);
    }

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
            totalDepositAmount = 0m, totalActualDepositAmount = 0m, totalFee = 0m, distinctAssetCount = 0,
            // Exact base-unit counterparts — see the note on the populated path below.
            totalDepositAmountBaseUnits = "0", totalActualDepositAmountBaseUnits = "0",
            totalFeeBaseUnits = "0", totalsDecimals = 0,
            items = Array.Empty<object>(),
        });

        // Free-text merchant-name search: resolve to ids first (Deposit/PaymentIntent never learn Merchant's
        // schema, §4.5). No match ⇒ short-circuit to an empty page, same pattern as an unknown coin below.
        IReadOnlyList<Guid>? merchantIds = null;
        if (!string.IsNullOrWhiteSpace(merchantName))
        {
            merchantIds = await merchants.SearchIdsByNameAsync(merchantName, http.RequestAborted);
            if (merchantIds.Count == 0)
                return EmptyPage();
        }

        // Effective-status filter (the same vocabulary the rows below report). Validated here so an unknown
        // value is a 400 rather than an empty page an operator would read as "nothing outstanding".
        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = status.Trim().ToLowerInvariant();
            if (!PaymentIntentEffectiveStatuses.IsKnown(normalisedStatus))
                return OpsResults.Bad(OpsErrorCodes.InvalidStatus, $"Unknown status '{status}'. Expected one of: {string.Join(", ", PaymentIntentEffectiveStatuses.All)}.");
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

        var filter = new PaymentIntentAdminFilter(
            merchantId, systemOrderNumber, merchantOrderNumber, receivingAddress, network, assetId, fromDate, toDate,
            MerchantIds: merchantIds, Status: normalisedStatus);
        var (items, total) = await paymentIntents.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var matchedDepositIds = items.Where(i => i.MatchedDepositId is not null).Select(i => i.MatchedDepositId!.Value).ToList();
        var depositSummaries = await deposits.GetByIdsAsync(matchedDepositIds, http.RequestAborted);

        // Totals across the WHOLE filtered set (every page, not just this one) — the summary row above the
        // table. totalsDecimals uses the coin filter's precision when one is set (the exact, correct case);
        // otherwise falls back to 6 like every per-row conversion below (§14 — see distinctAssetCount: if the
        // filtered set spans more than one asset, these sums are added together across different-decimal
        // assets and are only approximate, deliberately surfaced rather than silently hidden).
        var totals = await paymentIntents.GetTotalsAsync(filter, http.RequestAborted);
        var depositAmountTotals = await deposits.SumByIdsAsync(totals.MatchedDepositIds, http.RequestAborted);
        var totalsAsset = assetId is { } fixedAssetId ? await assets.FindByIdAsync(fixedAssetId, http.RequestAborted) : null;
        var totalsDecimals = totalsAsset?.Decimals ?? 6;

        var rows = await BuildRowsAsync(items, depositSummaries, assets, merchants, callbacks, http);

        return OpsResults.Ok(new
        {
            page,
            pageSize,
            totalCount = total,
            // Summary totals across the whole filtered set, not just this page (§14 — see the comment
            // above on totalsDecimals for the multi-asset caveat).
            totalTransactionRecords = total,
            totalDepositAmount = AmountConversion.ToDisplay(BigInteger.Parse(totals.TotalExpectedAmountBaseUnits), totalsDecimals),
            totalActualDepositAmount = AmountConversion.ToDisplay(BigInteger.Parse(depositAmountTotals.TotalAmountBaseUnits), totalsDecimals),
            totalFee = AmountConversion.ToDisplay(BigInteger.Parse(depositAmountTotals.TotalFeeBaseUnits), totalsDecimals),
            // The exact sums. These matter more than the per-row values do: a sum reaches a large
            // magnitude first, so it is the first place a double would start dropping digits.
            totalDepositAmountBaseUnits = totals.TotalExpectedAmountBaseUnits,
            totalActualDepositAmountBaseUnits = depositAmountTotals.TotalAmountBaseUnits,
            totalFeeBaseUnits = depositAmountTotals.TotalFeeBaseUnits,
            // The precision the three decimals above were converted at — 6 unless a coin filter
            // pinned it, which is the same caveat distinctAssetCount surfaces.
            totalsDecimals,
            distinctAssetCount = totals.DistinctAssetCount,
            items = rows,
        });
    }

    /// <summary>
    /// One deposit invoice by its system order number (REQ-6) — the deep-linkable detail view.
    ///
    /// <para>It returns the <b>identical row shape</b> the list returns, because it is built by the very same
    /// projection: the detail is the list's own row, fetched by id. That is deliberate — a separately written
    /// detail projection is exactly how a field ends up formatted one way on the table and another way on the
    /// record it opens.</para>
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid systemOrderNumber,
        IPaymentIntentDirectory paymentIntents,
        IDepositLookup deposits,
        ICallbackDeliveryQuery callbacks,
        IAssetCatalog assets,
        IMerchantDirectory merchants,
        HttpContext http)
    {
        // Reuses the search filter rather than adding a by-id Contract method: SystemOrderNumber is already a
        // unique narrowing, so this returns exactly zero or one row.
        var filter = new PaymentIntentAdminFilter(
            null, systemOrderNumber, null, null, null, null, null, null);
        var (items, _) = await paymentIntents.SearchAsync(filter, 1, 1, http.RequestAborted);

        if (items.Count == 0)
            return OpsResults.NotFound(OpsErrorCodes.NotFound, $"No deposit found with systemOrderNumber '{systemOrderNumber}'.");

        var matchedDepositIds = items.Where(i => i.MatchedDepositId is not null).Select(i => i.MatchedDepositId!.Value).ToList();
        var depositSummaries = await deposits.GetByIdsAsync(matchedDepositIds, http.RequestAborted);

        var rows = await BuildRowsAsync(items, depositSummaries, assets, merchants, callbacks, http);
        return OpsResults.Ok(rows[0]);
    }

    /// <summary>
    /// The single deposit-row projection, shared by the list and the detail endpoint so the two can never
    /// disagree about a field's name, precision, or formatting.
    /// </summary>
    private static async Task<List<object>> BuildRowsAsync(
        IReadOnlyList<PaymentIntentAdminRow> items,
        IReadOnlyDictionary<Guid, DepositSummaryView> depositSummaries,
        IAssetCatalog assets,
        IMerchantDirectory merchants,
        ICallbackDeliveryQuery callbacks,
        HttpContext http)
    {
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
                receivingAddress = intent.Address,
                network = intent.Chain.ToString(),
                coin = asset?.Symbol ?? "",
                expectedAmount = AmountConversion.ToDisplay(BigInteger.Parse(intent.ExpectedAmountBaseUnits), decimals),
                // The exact value, alongside the display decimal (§14). System.Text.Json writes a
                // `decimal` as a JSON number, and a JavaScript number is a double — so a browser has
                // already lost precision by the time JSON.parse returns, and no client-side care can
                // recover it. These carry the domain's own BigInteger representation, unconverted, so a
                // consumer that wants exactness has it. Additive: the decimals above are unchanged.
                expectedAmountBaseUnits = intent.ExpectedAmountBaseUnits,
                receivedAmount = matched is null ? (decimal?)null : AmountConversion.ToDisplay(BigInteger.Parse(matched.AmountBaseUnits), decimals),
                receivedAmountBaseUnits = matched?.AmountBaseUnits,
                txHash = matched?.TransactionHash,
                // The platform fee actually charged, snapshotted on the matched deposit at detection (§14). Null
                // until a deposit matches this invoice — no deposit, no fee charged yet.
                fee = matched is null ? (decimal?)null : AmountConversion.ToDisplay(BigInteger.Parse(matched.FeeBaseUnits), decimals),
                feeBaseUnits = matched?.FeeBaseUnits,
                // The asset's precision, so a consumer can format the base units above without a
                // second lookup against the asset catalog.
                decimals,
                confirms = matched?.Confirmations,
                type = "deposit",
                createdAt = intent.CreatedAt,
                status = intent.Status,
                callback = callback?.Status,
                callbackFailedCount = callback?.AttemptCount ?? 0,
                callbackNextAttemptAt = callback?.NextAttemptAt,
            });
        }

        return rows;
    }
}
