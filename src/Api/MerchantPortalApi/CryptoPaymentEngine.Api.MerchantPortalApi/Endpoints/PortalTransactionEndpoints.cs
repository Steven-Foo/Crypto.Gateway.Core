using System.Numerics;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The signed-in merchant's transaction history — payin (代收, deposit invoices), payout (代付, end-user
/// withdrawals) and cash-out (merchant earnings withdrawals). EVERY query is scoped to the session's tenant
/// (<c>PortalTenant.MerchantId</c>); the merchant id is never taken from the request, so a merchant physically
/// cannot page another merchant's transactions (§ tenant-isolation). Amounts show a display value + exact base
/// units (§14).
/// </summary>
public static class PortalTransactionEndpoints
{
    public static void MapPortalTransactionApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/portal/transactions/payin", PayinAsync).RequirePortalPermission(PortalPermissions.Transactions.View);
        app.MapGet("/api/v1/portal/transactions/payout", PayoutAsync).RequirePortalPermission(PortalPermissions.Transactions.View);
        app.MapGet("/api/v1/portal/transactions/cash-out", CashOutAsync).RequirePortalPermission(PortalPermissions.Transactions.View);
    }

    private static async Task<IResult> PayinAsync(
        IPaymentIntentDirectory intents,
        IDepositLookup deposits,
        IAssetCatalog assets,
        HttpContext http,
        string? merchantOrderNumber = null,
        string? receivingAddress = null,
        Chain? network = null,
        string? coin = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        string? status = null,
        int page = 1,
        int pageSize = 50)
    {
        Clamp(ref page, ref pageSize);

        var (assetId, coinError) = await ResolveCoinAsync(assets, network, coin, http);
        if (coinError is not null)
            return coinError;

        // Same contract as the payout list: filter on the effective status the rows report, in SQL, and
        // reject an unknown value rather than returning an unfiltered page under a filtered request.
        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = status.Trim().ToLowerInvariant();
            if (!PaymentIntentEffectiveStatuses.IsKnown(normalisedStatus))
                return Bad(PortalErrorCodes.InvalidStatus,
                    $"Unknown status '{status}'. Expected one of: {string.Join(", ", PaymentIntentEffectiveStatuses.All)}.");
        }

        // MerchantId is the SESSION's tenant — never a request value.
        var filter = new PaymentIntentAdminFilter(
            PortalTenant.MerchantId(http), SystemOrderNumber: null, merchantOrderNumber, receivingAddress, network, assetId,
            fromDate, toDate, MerchantIds: null, Status: normalisedStatus);
        var (items, total) = await intents.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var matchedIds = items.Where(i => i.MatchedDepositId is { } id && id != Guid.Empty)
            .Select(i => i.MatchedDepositId!.Value).ToList();
        var matched = matchedIds.Count == 0
            ? new Dictionary<Guid, DepositSummaryView>()
            : await deposits.GetByIdsAsync(matchedIds, http.RequestAborted);

        var decimalsByAsset = new Dictionary<Guid, int>();
        var rows = new List<object>(items.Count);
        foreach (var i in items)
        {
            var decimals = await DecimalsAsync(assets, decimalsByAsset, i.AssetId, http);
            var deposit = i.MatchedDepositId is { } id ? matched.GetValueOrDefault(id) : null;

            rows.Add(new
            {
                systemOrderNumber = i.PublicReference,
                merchantOrderNumber = i.MerchantTransactionId,
                network = i.Chain.ToString(),
                coin = await SymbolAsync(assets, i.AssetId, http),
                address = i.Address,
                expectedAmount = ToDisplay(i.ExpectedAmountBaseUnits, decimals),
                expectedAmountBaseUnits = i.ExpectedAmountBaseUnits,
                receivedAmount = deposit is null ? (decimal?)null : ToDisplay(deposit.AmountBaseUnits, decimals),
                receivedAmountBaseUnits = deposit?.AmountBaseUnits,
                confirmations = deposit?.Confirmations,
                txHash = deposit?.TransactionHash,
                status = i.Status,
                // "Customer" | "MerchantTopUp" — a top-up is the merchant funding its own balance, priced on
                // its own (zero-default) fee schedule and exempt from the T+N hold. Without this the two are
                // indistinguishable here, and a merchant cannot reconcile customer revenue against its own
                // deposits.
                kind = i.Kind,
                createdAt = i.CreatedAt,
            });
        }

        return Paged(page, pageSize, total, rows);
    }

    private static Task<IResult> PayoutAsync(
        IWithdrawalDirectory withdrawals, IAssetCatalog assets, HttpContext http,
        string? merchantOrderNumber = null, string? receivingAddress = null, Chain? network = null, string? coin = null,
        DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, string? status = null, int page = 1, int pageSize = 50) =>
        WithdrawalsAsync(withdrawals, assets, http, "User", "payout", merchantOrderNumber, receivingAddress, network, coin, fromDate, toDate, status, page, pageSize);

    private static Task<IResult> CashOutAsync(
        IWithdrawalDirectory withdrawals, IAssetCatalog assets, HttpContext http,
        string? merchantOrderNumber = null, string? receivingAddress = null, Chain? network = null, string? coin = null,
        DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, string? status = null, int page = 1, int pageSize = 50) =>
        WithdrawalsAsync(withdrawals, assets, http, "Merchant", "cash_out", merchantOrderNumber, receivingAddress, network, coin, fromDate, toDate, status, page, pageSize);

    private static async Task<IResult> WithdrawalsAsync(
        IWithdrawalDirectory withdrawals, IAssetCatalog assets, HttpContext http, string kind, string type,
        string? merchantOrderNumber, string? receivingAddress, Chain? network, string? coin,
        DateTimeOffset? fromDate, DateTimeOffset? toDate, string? status, int page, int pageSize)
    {
        Clamp(ref page, ref pageSize);

        var (assetId, coinError) = await ResolveCoinAsync(assets, network, coin, http);
        if (coinError is not null)
            return coinError;

        // Filtering on the same effective status the rows report, pushed into SQL so totalCount reflects it.
        // Client-side filtering cannot work here: page 1 of all payouts may hold none of the status being
        // looked for, so an approval queue would render empty while work waits pages back — a queue that
        // hides work is worse than no queue. An unrecognised value is a 400 rather than a silently unfiltered
        // page, which would read to the caller as "these are the matching ones".
        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = status.Trim().ToLowerInvariant();
            if (!WithdrawalEffectiveStatuses.IsKnown(normalisedStatus))
                return Bad(PortalErrorCodes.InvalidStatus,
                    $"Unknown status '{status}'. Expected one of: {string.Join(", ", WithdrawalEffectiveStatuses.All)}.");
        }

        var filter = new WithdrawalAdminFilter(
            PortalTenant.MerchantId(http), SystemOrderNumber: null, merchantOrderNumber, receivingAddress, network, assetId,
            fromDate, toDate, kind, MerchantIds: null, Status: normalisedStatus);
        var (items, total) = await withdrawals.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var decimalsByAsset = new Dictionary<Guid, int>();
        var rows = new List<object>(items.Count);
        foreach (var w in items)
        {
            var decimals = await DecimalsAsync(assets, decimalsByAsset, w.AssetId, http);
            rows.Add(new
            {
                systemOrderNumber = w.WithdrawalId,
                merchantOrderNumber = w.MerchantTransactionId,
                network = w.Chain.ToString(),
                coin = await SymbolAsync(assets, w.AssetId, http),
                receivingAddress = w.DestinationAddress,
                amount = ToDisplay(w.AmountBaseUnits, decimals),
                amountBaseUnits = w.AmountBaseUnits,
                fee = ToDisplay(w.FeeBaseUnits, decimals),
                feeBaseUnits = w.FeeBaseUnits,
                confirms = w.Confirmations,
                txHash = w.TransactionHash,
                status = w.Status,
                statusReason = w.StatusReason,
                type,
                createdAt = w.CreatedAt,
            });
        }

        return Paged(page, pageSize, total, rows);
    }

    // ── helpers ──

    private static void Clamp(ref int page, ref int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;
    }

    private static async Task<(Guid? AssetId, IResult? Error)> ResolveCoinAsync(
        IAssetCatalog assets, Chain? network, string? coin, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(coin))
            return (null, null);
        if (network is null)
            return (null, Bad(PortalErrorCodes.NetworkRequired, "network is required when filtering by coin."));

        var asset = await assets.FindAsync(network.Value, coin.Trim().ToUpperInvariant(), http.RequestAborted);
        // Unknown coin ⇒ a filter that matches nothing; signal via a sentinel empty-guid so the search returns none.
        return asset is null ? (Guid.Empty, null) : (asset.AssetId, null);
    }

    private static async Task<int> DecimalsAsync(IAssetCatalog assets, Dictionary<Guid, int> cache, Guid assetId, HttpContext http)
    {
        if (cache.TryGetValue(assetId, out var d))
            return d;
        var asset = await assets.FindByIdAsync(assetId, http.RequestAborted);
        d = asset?.Decimals ?? 6;
        cache[assetId] = d;
        return d;
    }

    private static async Task<string> SymbolAsync(IAssetCatalog assets, Guid assetId, HttpContext http) =>
        (await assets.FindByIdAsync(assetId, http.RequestAborted))?.Symbol ?? "";

    private static decimal ToDisplay(string baseUnits, int decimals) =>
        BigInteger.TryParse(baseUnits, out var v) ? AmountConversion.ToDisplay(v, decimals) : 0m;

    private static IResult Paged(int page, int pageSize, int total, IReadOnlyList<object> rows) =>
        PortalResults.Ok(new { page, pageSize, totalCount = total, items = rows });

    private static IResult Bad(string errorCode, string message) => PortalResults.Bad(errorCode, message);
}
