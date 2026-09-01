using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// One screen for every movement of company funds across the custody boundary: money an admin put IN (hot
/// wallet top-ups) and money an admin paid OUT on a merchant's behalf (settlements paid from a company
/// wallet). Both are recorded rather than executed by this system, so this is where an operator checks that
/// the records match what actually happened.
///
/// <para>It also separates the two things that are easy to conflate: <b>earnings</b> are fee revenue and
/// nothing else, while these movements are company funds deployed. Adding settlements to earnings would count
/// money going out as money coming in — so the summary reports them as distinct blocks, never as one number.</para>
/// </summary>
public static class OpsSettlementActivityEndpoints
{
    public static void MapOpsSettlementActivityApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/settlement-activity", GetAsync)
            .RequirePermission(OpsPermissions.Treasury.Manage);

    private static async Task<IResult> GetAsync(
        IHotWalletTopUpRepository topUps,
        IWithdrawalDirectory withdrawals,
        ILedgerQuery ledger,
        IAssetCatalog assets,
        HttpContext http,
        string? chain = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 200);

        Chain? parsedChain = null;
        if (!string.IsNullOrWhiteSpace(chain))
        {
            if (!Enum.TryParse<Chain>(chain, ignoreCase: true, out var c))
                return OpsResults.Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{chain}'.");
            parsedChain = c;
        }

        var asset = await assets.FindAsync(parsedChain ?? Chain.Tron, "USDT", http.RequestAborted);
        var decimals = asset?.Decimals ?? 6;

        var (topUpRows, topUpTotal) = await topUps.SearchAsync(parsedChain, page, pageSize, http.RequestAborted);

        // Settlements that have actually been paid — the outward half of the same story.
        var (settlements, settlementTotal) = await withdrawals.SearchAsync(
            new WithdrawalAdminFilter(
                MerchantId: null, SystemOrderNumber: null, MerchantOrderNumber: null, ReceivingAddress: null,
                Network: parsedChain, AssetId: null, FromDate: null, ToDate: null,
                Kind: "Merchant", Status: "finance_settled"),
            page, pageSize, http.RequestAborted);

        // One shape for both kinds so they can be interleaved chronologically — an operator reconciling the
        // company's position reads them as a single sequence, not two lists they have to merge by eye.
        var items = new List<ActivityRow>();

        foreach (var t in topUpRows)
            items.Add(new ActivityRow(
                Type: "top_up",
                Direction: "in",                        // company funds INTO platform custody
                RecordedAt: t.RecordedAt,
                RecordedBy: t.RecordedBy,
                Network: t.Chain.ToString(),
                Amount: AmountConversion.ToDisplay(t.Amount, decimals),
                AmountBaseUnits: t.Amount.ToString(CultureInfo.InvariantCulture),
                TxHash: t.TransactionHash,
                SourceAddress: t.SourceAddress,
                DestinationAddress: t.TargetAddress,
                MerchantId: null));

        foreach (var s in settlements)
            items.Add(new ActivityRow(
                Type: "merchant_settlement",
                Direction: "out",                       // company funds paid to a merchant, outside custody
                RecordedAt: s.CompletedAt ?? s.CreatedAt,
                RecordedBy: s.CompletedBy,
                Network: s.Chain.ToString(),
                Amount: ToDisplay(s.AmountBaseUnits, decimals),
                AmountBaseUnits: s.AmountBaseUnits,
                TxHash: s.TransactionHash,
                SourceAddress: s.SettlementSourceAddress,
                DestinationAddress: s.DestinationAddress,
                MerchantId: s.MerchantId));

        var ordered = items.OrderByDescending(i => i.RecordedAt).ToList();

        // Running totals straight from the ledger, not summed from this page — a paged view must never be
        // mistaken for the whole picture, and these figures have to agree with the custody screen.
        var contributed = asset is null
            ? BigInteger.Zero
            : await ledger.GetWithdrawalWalletTopUpTotalAsync(asset.AssetId, http.RequestAborted);

        var paidExternally = asset is null
            ? BigInteger.Zero
            : await ledger.GetExternalSettlementTotalAsync(asset.AssetId, http.RequestAborted);

        return OpsResults.Ok(new
        {
            page,
            pageSize,
            totalCount = topUpTotal + settlementTotal,
            items = ordered,
            summary = new
            {
                coin = asset?.Symbol,
                // Company funds moved INTO custody to keep payouts running.
                floatContributed = AmountConversion.ToDisplay(contributed, decimals),
                floatContributedBaseUnits = contributed.ToString(CultureInfo.InvariantCulture),
                // Company funds paid OUT to merchants from wallets outside custody.
                settlementsPaidExternally = AmountConversion.ToDisplay(paidExternally, decimals),
                settlementsPaidExternallyBaseUnits = paidExternally.ToString(CultureInfo.InvariantCulture),
                // Deliberately NOT combined with either figure above: earnings are fee revenue, and treating
                // an outward settlement as income would be counting money leaving as money arriving.
                note = "Earnings are fee revenue only; these are company funds deployed, not income.",
            },
        });
    }

    private static decimal ToDisplay(string baseUnits, int decimals) =>
        BigInteger.TryParse(baseUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? AmountConversion.ToDisplay(v, decimals)
            : 0m;

    /// <summary>A movement of company funds across the custody boundary, in either direction.</summary>
    private sealed record ActivityRow(
        string Type,
        string Direction,
        DateTimeOffset RecordedAt,
        string? RecordedBy,
        string Network,
        decimal Amount,
        string AmountBaseUnits,
        string? TxHash,
        string? SourceAddress,
        string? DestinationAddress,
        Guid? MerchantId);
}
