using System.Globalization;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The signed-in merchant's balances, per asset — derived from the immutable ledger (§14), scoped to the
/// session's tenant. Distinguishes the two balance kinds the ledger gives cleanly and never collapses them
/// (§ money-rules): <b>available</b> (spendable now — reserved funds already excluded) and <b>settled</b>
/// (withdrawable — available minus deposits still maturing past the merchant's T+N settlement period). Amounts
/// carry a display value plus the exact base-unit integer.
/// </summary>
public static class PortalFundsEndpoints
{
    public static void MapPortalFundsApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/portal/funds", GetAsync).RequirePortalPermission(PortalPermissions.Overview.View);

    private static async Task<IResult> GetAsync(
        ILedgerQuery ledger, IAssetCatalog assets, IMerchantDirectory merchants, HttpContext http)
    {
        var merchantId = PortalTenant.MerchantId(http);

        var merchant = await merchants.FindByIdAsync(merchantId, http.RequestAborted);
        var settlementDelayDays = merchant?.SettlementDelayDays ?? 0;

        // A deposit dated on/after this cutoff is still maturing (excluded from settled). cutoff =
        // start-of-today(UTC) + (1 − N) days — mirrors the Ledger's settled-balance contract. N=0 ⇒ cutoff is
        // tomorrow 00:00Z, so everything already booked is settled.
        var now = DateTimeOffset.UtcNow;
        var startOfToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var unmaturedCutoffUtc = startOfToday.AddDays(1 - settlementDelayDays);

        var catalog = await assets.GetActiveAsync(http.RequestAborted);

        var rows = new List<object>(catalog.Count);
        foreach (var asset in catalog)
        {
            var available = await ledger.GetMerchantBalanceAsync(merchantId, asset.AssetId, http.RequestAborted);
            var settled = await ledger.GetMerchantSettledBalanceAsync(merchantId, asset.AssetId, unmaturedCutoffUtc, http.RequestAborted);

            rows.Add(new
            {
                assetId = asset.AssetId,
                coin = asset.Symbol,
                network = asset.Chain.ToString(),
                available = AmountConversion.ToDisplay(available, asset.Decimals),
                availableBaseUnits = available.ToString(CultureInfo.InvariantCulture),
                settled = AmountConversion.ToDisplay(settled, asset.Decimals),
                settledBaseUnits = settled.ToString(CultureInfo.InvariantCulture),
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { settlementDelayDays, items = rows },
            error = (string?)null,
        });
    }
}
