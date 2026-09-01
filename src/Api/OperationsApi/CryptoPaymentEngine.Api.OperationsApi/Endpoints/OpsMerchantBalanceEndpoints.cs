using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Application;
using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-initiated manual credit/debit to a merchant's ledger balance — a compensating correction, not backed
/// by a real on-chain deposit/withdrawal (e.g. a support-ticket goodwill credit, or clawing back an over-credit).
/// Gated on <see cref="OpsPermissions.Balances.Adjust"/>, deliberately granted to Admin only (never bundled with
/// <see cref="OpsPermissions.Merchants.Manage"/>): this moves real merchant liability, unlike every other merchant
/// admin action. Posts immediately — no second-approver threshold (may be revisited if adjustment volume/size
/// grows). <see cref="LedgerPoster"/> keeps the money-critical accounting policy (which accounts move, and why a
/// manual correction never touches <c>TreasuryAsset</c>); this endpoint only converts display↔base units at the
/// edge (§14) and enforces the mandatory <c>reason</c> for audit.
/// </summary>
public static class OpsMerchantBalanceEndpoints
{
    public static void MapOpsMerchantBalanceApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/merchants/{id:guid}/balance/credit", CreditAsync).RequirePermission(OpsPermissions.Balances.Adjust);
        app.MapPost("/api/v1/ops/merchants/{id:guid}/balance/debit", DebitAsync).RequirePermission(OpsPermissions.Balances.Adjust);
        app.MapGet("/api/v1/ops/merchants/{id:guid}/balance/history", HistoryAsync).RequirePermission(OpsPermissions.Merchants.View);
    }

    private static async Task<IResult> CreditAsync(
        Guid id, AdjustMerchantBalanceRequest request, IMerchantRegistrar registrar, IAssetCatalog assets,
        ILedgerPoster ledger, IAuditLogger audit, HttpContext http)
    {
        var (asset, amount, error) = await ResolveAsync(id, request, registrar, assets, http);
        if (error is not null)
            return error;

        var result = await ledger.CreditMerchantBalanceAsync(
            new CreditMerchantBalanceCommand(id, asset!.AssetId, amount, request.Reason, request.AdjustmentId),
            http.RequestAborted);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.balance_credited", "Merchant", id.ToString(),
            $"{request.Amount} {asset.Symbol} ({asset.Chain}): {request.Reason}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = asset.Chain.ToString(), outcome = result.Value.ToString() },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }

    private static async Task<IResult> DebitAsync(
        Guid id, AdjustMerchantBalanceRequest request, IMerchantRegistrar registrar, IAssetCatalog assets,
        ILedgerPoster ledger, IAuditLogger audit, HttpContext http)
    {
        var (asset, amount, error) = await ResolveAsync(id, request, registrar, assets, http);
        if (error is not null)
            return error;

        var result = await ledger.DebitMerchantBalanceAsync(
            new DebitMerchantBalanceCommand(id, asset!.AssetId, amount, request.Reason, request.AdjustmentId),
            http.RequestAborted);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.balance_debited", "Merchant", id.ToString(),
            $"{request.Amount} {asset.Symbol} ({asset.Chain}): {request.Reason}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { merchantId = id, assetId = asset.AssetId, coin = asset.Symbol, network = asset.Chain.ToString(), outcome = result.Value.ToString() },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }

    /// <summary>
    /// A merchant's full balance history ("account statement") — every real deposit/reversal, withdrawal
    /// reserve/release, and manual credit/debit, newest first. Read-only, gated the same as the rest of the
    /// merchant details payload (<see cref="OpsPermissions.Merchants.View"/>) — this is a report, not the
    /// sensitive money-moving action (that's <see cref="OpsPermissions.Balances.Adjust"/>, above).
    /// </summary>
    private static async Task<IResult> HistoryAsync(
        Guid id, ILedgerQuery ledger, IAssetCatalog assets, HttpContext http,
        string? chain = null, string? coin = null,
        DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null,
        int page = 1, int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        Guid? assetId = null;
        if (!string.IsNullOrWhiteSpace(coin))
        {
            if (string.IsNullOrWhiteSpace(chain) || !Enum.TryParse<Chain>(chain, ignoreCase: true, out var parsedChain))
                return OpsResults.Bad(OpsErrorCodes.NetworkRequired, "chain is required (and must be a known chain) when coin is set.");

            var asset = await assets.FindAsync(parsedChain, coin.Trim().ToUpperInvariant(), http.RequestAborted);
            if (asset is null)
                return OpsResults.Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{coin}' on {parsedChain}.");

            assetId = asset.AssetId;
        }

        var (items, total) = await ledger.GetMerchantBalanceHistoryAsync(id, assetId, fromDate, toDate, page, pageSize, http.RequestAborted);

        // Resolve each row's coin/network/decimals once per distinct asset, not once per row.
        var assetsById = new Dictionary<Guid, AssetDto?>();
        foreach (var distinctAssetId in items.Select(i => i.AssetId).Distinct())
            assetsById[distinctAssetId] = await assets.FindByIdAsync(distinctAssetId, http.RequestAborted);

        return OpsResults.Ok(new
        {
                merchantId = id,
                page,
                pageSize,
                totalCount = total,
                items = items.Select(i =>
                {
                    var asset = assetsById.GetValueOrDefault(i.AssetId);
                    var decimals = asset?.Decimals ?? 6;
                    return new
                    {
                        journalId = i.JournalId,
                        type = FriendlyType(i.ReferenceType, i.Direction),
                        referenceType = i.ReferenceType,
                        referenceId = i.ReferenceId,
                        direction = i.Direction,
                        amount = AmountConversion.ToDisplay(i.Amount, decimals),
                        amountBaseUnits = i.Amount.ToString(),
                        assetId = i.AssetId,
                        coin = asset?.Symbol,
                        network = asset?.Chain.ToString(),
                        reason = i.Description,
                        createdAt = i.CreatedAt,
                    };
                }),
        });
    }

    /// <summary>
    /// A frontend-friendly category for a balance-history row, derived from the raw
    /// <c>(ReferenceType, Direction)</c> pair. <c>Adjustment</c> is the one reference type that needs
    /// <paramref name="direction"/> to disambiguate — every other reference type only ever moves the
    /// merchant's liability in one fixed direction.
    /// </summary>
    private static string FriendlyType(string referenceType, string direction) => (referenceType, direction) switch
    {
        ("Deposit", _) => "deposit",
        ("DepositReversal", _) => "deposit_reversal",
        ("WithdrawalReserve", _) => "withdrawal_reserve",
        ("WithdrawalRelease", _) => "withdrawal_release",
        ("Adjustment", "Credit") => "manual_credit",
        ("Adjustment", "Debit") => "manual_debit",
        // Everything else is platform-side accounting that never posts a line against a merchant's liability
        // (WithdrawalSettle, Sweep, GasCost, WithdrawalWalletTopUp), so it cannot reach this projection —
        // the query INNER JOINs that account. Guarded by a test rather than by redundant switch arms.
        _ => "other",
    };

    /// <summary>Shared validation: merchant exists, chain/coin resolve to a known asset, amount is a storable
    /// positive base-unit value at the asset's precision. Returns a non-null <c>Error</c> IResult on any failure.</summary>
    private static async Task<(AssetDto? Asset, System.Numerics.BigInteger Amount, IResult? Error)> ResolveAsync(
        Guid merchantId, AdjustMerchantBalanceRequest request, IMerchantRegistrar registrar, IAssetCatalog assets, HttpContext http)
    {
        var merchant = await registrar.GetAsync(merchantId, http.RequestAborted);
        if (merchant.IsFailure)
            return (null, default, OpsResults.NotFound(OpsErrorCodes.NotFound, "Merchant not found."));

        if (!Enum.TryParse<Chain>(request.Chain, ignoreCase: true, out var chain))
            return (null, default, OpsResults.Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{request.Chain}'."));

        var asset = await assets.FindAsync(chain, request.Coin.Trim().ToUpperInvariant(), http.RequestAborted);
        if (asset is null)
            return (null, default, OpsResults.Bad(OpsErrorCodes.InvalidAsset, $"Unknown coin '{request.Coin}' on {chain}."));

        if (!AmountConversion.TryToBaseUnits(request.Amount, asset.Decimals, out var amount))
            return (null, default, OpsResults.Bad(OpsErrorCodes.InvalidAmount, "amount must be positive and no finer than the asset's precision."));

        return (asset, amount, null);
    }

}
