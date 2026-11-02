using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing transaction history — read straight from the immutable ledger, not from
/// Deposit/Withdrawal/PaymentIntent's own tables (§ ILedgerQuery.GetJournalsAsync).
///
/// <c>transactionId</c> search resolves through PaymentIntent only (deposit-side) today — withdrawal-side
/// resolution (by <c>Withdrawal.MerchantTransactionId</c>) isn't wired in yet, even though this host does
/// compose the Withdrawal module. Add it the same way once Ops needs to search withdrawal transactions by ID.
/// </summary>
public static class OpsTransactionEndpoints
{
    public static void MapOpsTransactionApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/transactions", ListAsync).RequirePermission(OpsPermissions.Transactions.View);

    private static async Task<IResult> ListAsync(
        ILedgerQuery ledger,
        IPaymentIntentDirectory paymentIntents,
        IAssetCatalog assets,
        CryptoPaymentEngine.Gateway.Core.Merchant.Contracts.IMerchantDirectory merchants,
        CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application.IAuditQuery audit,
        HttpContext http,
        Guid? merchantId = null,
        string? transactionId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        // Resolving a merchant's own transaction string only makes sense scoped to that merchant — both
        // PaymentIntent's and Withdrawal's merchant transaction ids are unique per-merchant, not globally.
        Guid? referenceId = null;
        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            if (merchantId is null)
                return OpsResults.Bad(OpsErrorCodes.MerchantIdRequired, "merchantId is required when filtering by transactionId.");

            referenceId = await paymentIntents.FindMatchedDepositIdAsync(merchantId.Value, transactionId, http.RequestAborted);
            if (referenceId is null)
                return Results.Ok(new { isSuccess = true, data = new { page, pageSize, totalCount = 0, items = Array.Empty<object>() }, error = (string?)null, errorCode = (string?)null });
        }

        var (items, total) = await ledger.GetJournalsAsync(merchantId, referenceId, fromDate, toDate, page, pageSize, http.RequestAborted);

        // Ledger amounts stay exact base-unit integers on the wire (§14) — this screen deliberately does NOT
        // convert them. But a consumer cannot format a base-unit integer without knowing the asset's
        // precision, so each row carries its own symbol + decimals rather than forcing a second lookup.
        // Resolved once per distinct asset, not per row.
        var assetCache = new Dictionary<Guid, AssetDto?>();
        foreach (var assetId in items.Select(i => i.AssetId).Distinct())
            assetCache[assetId] = await assets.FindByIdAsync(assetId, http.RequestAborted);

        var orderNumberOf = await LedgerOrderNumbers.ResolveAsync(
            items.Select(i => (i.ReferenceType, i.ReferenceId)), paymentIntents, http.RequestAborted);
        var operatorOf = await LedgerOperators.ResolveAsync(
            items.Select(i => (i.ReferenceType, i.ReferenceId)), audit, http.RequestAborted);
        var merchantNames = await merchants.GetNamesByIdsAsync(
            items.Where(i => i.MerchantId is not null).Select(i => i.MerchantId!.Value).Distinct().ToList(),
            http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                page,
                pageSize,
                totalCount = total,
                items = items.Select(i => new
                {
                    journalId = i.JournalId,
                    // Which merchant the row belongs to — needed when no merchantId filter is applied.
                    merchantId = i.MerchantId,
                    merchantName = i.MerchantId is { } mid ? merchantNames.GetValueOrDefault(mid) : null,
                    referenceType = i.ReferenceType,
                    referenceId = i.ReferenceId,
                    // The order number (系统流水号) the deposit/withdrawal screens show — see LedgerOrderNumbers.
                    systemOrderNumber = orderNumberOf(i.ReferenceType, i.ReferenceId),
                    assetId = i.AssetId,
                    // Null for a gas-denominated journal (§5c GasCost): the gas AssetId is deliberately kept
                    // OUT of the deposit catalog, so it has no symbol here. A consumer must render those rows
                    // as raw base units rather than assume a precision.
                    coin = assetCache.GetValueOrDefault(i.AssetId)?.Symbol,
                    decimals = assetCache.GetValueOrDefault(i.AssetId)?.Decimals,
                    description = i.Description,
                    direction = i.Direction,
                    amount = i.Amount.ToString(),
                    // That row's merchant's balance in that coin just before / after this entry — base-unit
                    // integer strings like `amount`. Per row, so meaningful in a mixed-merchant list too.
                    balanceBefore = i.BalanceBefore?.ToString(),
                    balanceAfter = i.BalanceAfter?.ToString(),
                    // Staff member behind a manual adjustment (null for every system-driven row).
                    @operator = operatorOf(i.ReferenceType, i.ReferenceId),
                    createdAt = i.CreatedAt,
                }),
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }
}
