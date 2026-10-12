using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing wallet browsing and holds. Suspend/resume place or lift a temporary hold on one deposit
/// address (e.g. it received an unexpected/off-flow transfer and is being held for investigation) — never
/// a permanent decommission, and never touches the merchant assignment (see <c>Wallet.Suspend</c>).
/// </summary>
public static class OpsWalletEndpoints
{
    public static void MapOpsWalletApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/wallets", SearchAsync).RequirePermission(OpsPermissions.Wallets.View);
        app.MapGet("/api/v1/ops/wallets/{id:guid}", GetAsync).RequirePermission(OpsPermissions.Wallets.View);
        app.MapPost("/api/v1/ops/wallets/{id:guid}/suspend", SuspendAsync).RequirePermission(OpsPermissions.Wallets.Manage)
            .RequireTwoFactor(GuardedActions.WalletSuspend);
        app.MapPost("/api/v1/ops/wallets/{id:guid}/resume", ResumeAsync).RequirePermission(OpsPermissions.Wallets.Manage)
            .RequireTwoFactor(GuardedActions.WalletSuspend);
    }

    private static async Task<IResult> SearchAsync(
        IWalletAdminService wallets,
        IMerchantDirectory merchants,
        IAssetCatalog assets,
        IDepositLookup deposits,
        ISweepDirectory sweeps,
        HttpContext http,
        Guid? merchantId = null,
        string? address = null,
        Chain? chain = null,
        string? status = null,
        string? walletType = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        WalletStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WalletStatus>(status, ignoreCase: true, out var parsed))
                return OpsResults.Bad(OpsErrorCodes.InvalidStatus, $"Unknown status '{status}'. Expected Active, Disabled, or Suspended.");

            statusFilter = parsed;
        }

        // Rejected rather than ignored: silently dropping an unrecognised walletType would widen the result
        // set to every wallet while the operator believes they are looking at one kind.
        WalletType? walletTypeFilter = null;
        if (!string.IsNullOrWhiteSpace(walletType))
        {
            if (!Enum.TryParse<WalletType>(walletType, ignoreCase: true, out var parsedType))
                return OpsResults.Bad(OpsErrorCodes.InvalidWalletType, $"Unknown walletType '{walletType}'. Expected one of: {string.Join(", ", Enum.GetNames<WalletType>())}.");

            walletTypeFilter = parsedType;
        }

        var filter = new WalletAdminFilter(
            merchantId, address?.Trim(), chain, statusFilter, WalletType: walletTypeFilter);
        var (items, total) = await wallets.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var rows = await BuildRowsAsync(items, merchants, assets, deposits, sweeps, http);
        return OpsResults.Ok(new { page, pageSize, totalCount = total, items = rows });
    }

    /// <summary>
    /// One wallet by id (REQ-6) — the deep-linkable detail view. Returns the <b>identical row shape</b> the
    /// list returns, built by the very same projection, so the table and the record it opens can never
    /// disagree about a field.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id,
        IWalletAdminService wallets,
        IMerchantDirectory merchants,
        IAssetCatalog assets,
        IDepositLookup deposits,
        ISweepDirectory sweeps,
        IBalanceReader balances,
        INativeBalanceReader nativeBalances,
        ILogger<WalletDetailView> logger,
        HttpContext http)
    {
        // Reuses the search filter rather than adding a by-id Contract method: WalletId is a unique
        // narrowing, so this returns exactly zero or one row.
        var (items, _) = await wallets.SearchAsync(
            new WalletAdminFilter(null, null, null, null, WalletId: id), 1, 1, http.RequestAborted);

        if (items.Count == 0)
            return OpsResults.NotFound(OpsErrorCodes.NotFound, $"No wallet found with id '{id}'.");

        var row = (await BuildRowsAsync(items, merchants, assets, deposits, sweeps, http))[0];
        var wallet = items[0];
        var asset = await assets.FindAsync(wallet.Chain, "USDT", http.RequestAborted);

        // The detail-only figures. Two are LIVE chain reads (one address, so affordable here, unlike the list).
        // Unreadable ⇒ null, never 0: "unknown" and "empty" call for opposite actions, and an empty-looking
        // address that in fact holds funds is the worse mistake — the same rule as the hot-pool screen.
        BigInteger? onChain = null;
        if (asset is not null)
        {
            try { onChain = await balances.GetBalanceAsync(wallet.Chain, wallet.Address, asset.AssetId, http.RequestAborted); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Wallet {WalletId}: on-chain balance unreadable.", wallet.WalletId); }
        }

        var gasCoin = NativeCoins.For(wallet.Chain);
        BigInteger? gas = null;
        try { gas = await nativeBalances.GetNativeBalanceAsync(wallet.Chain, wallet.Address, http.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Wallet {WalletId}: gas balance unreadable.", wallet.WalletId); }

        SweptTotal? swept = null;
        if (asset is not null)
        {
            var totals = await sweeps.GetConfirmedTotalsByWalletAsync([wallet.WalletId], asset.AssetId, http.RequestAborted);
            swept = totals.GetValueOrDefault(wallet.WalletId);
        }

        var sweptTotal = swept is null ? BigInteger.Zero : BigInteger.Parse(swept.TotalBaseUnits, CultureInfo.InvariantCulture);

        return OpsResults.Ok(new WalletDetailView(row)
        {
            OnChainBalance = onChain is { } o && asset is not null ? AmountConversion.ToDisplay(o, asset.Decimals) : null,
            OnChainBalanceBaseUnits = onChain?.ToString(CultureInfo.InvariantCulture),
            GasSymbol = gasCoin.Symbol,
            GasDecimals = gasCoin.Decimals,
            GasBalance = gas is { } g ? AmountConversion.ToDisplay(g, gasCoin.Decimals) : null,
            GasBalanceBaseUnits = gas?.ToString(CultureInfo.InvariantCulture),
            TotalSwept = asset is not null ? AmountConversion.ToDisplay(sweptTotal, asset.Decimals) : null,
            TotalSweptBaseUnits = asset is not null ? sweptTotal.ToString(CultureInfo.InvariantCulture) : null,
            SweepCount = swept?.Count ?? 0,
        });
    }

    /// <summary>
    /// The single wallet-row projection, shared by the list and the detail endpoint.
    ///
    /// <para><b>The recorded balance</b> (<c>balance</c>) is what our own records say sits on a deposit address
    /// waiting to be swept: received (confirmed + dust) − swept (confirmed sweeps). It is NOT a ledger figure — the
    /// ledger tracks money by owner, never by address (§8) — and no chain call is made, so it is cheap enough for
    /// every row of a page (two batched queries, not one per row). It's only meaningful for <c>Deposit</c>
    /// wallets, whose money arrives as recorded deposits and leaves as recorded sweeps; for other wallet types
    /// (hot pool, staking…) money moves in ways these records don't cover, so it is null rather than misleading.
    /// The detail page adds the live on-chain balance next to it, so staff can see when the two differ.</para>
    /// </summary>
    private static async Task<List<WalletRowView>> BuildRowsAsync(
        IReadOnlyList<WalletAdminRow> items,
        IMerchantDirectory merchants,
        IAssetCatalog assets,
        IDepositLookup deposits,
        ISweepDirectory sweeps,
        HttpContext http)
    {
        // MerchantId is nullable here — platform wallets (hot pool, staking, treasury, etc.) have none, only
        // deposit wallets are ever assigned to a merchant.
        var merchantNames = await merchants.GetNamesByIdsAsync(
            items.Where(w => w.MerchantId is not null).Select(w => w.MerchantId!.Value).Distinct().ToList(),
            http.RequestAborted);

        // Recorded balances, per chain: one deposit query + one sweep query for the whole page.
        var recorded = new Dictionary<Guid, (BigInteger Balance, int Decimals, string Coin)>();
        foreach (var byChain in items.Where(w => w.WalletType == nameof(WalletType.Deposit)).GroupBy(w => w.Chain))
        {
            var asset = await assets.FindAsync(byChain.Key, "USDT", http.RequestAborted);
            if (asset is null)
                continue;

            var received = await deposits.SumReceivedByAddressesAsync(
                byChain.Key, asset.AssetId, byChain.Select(w => w.Address).Distinct().ToList(), http.RequestAborted);
            var swept = await sweeps.GetConfirmedTotalsByWalletAsync(
                byChain.Select(w => w.WalletId).ToList(), asset.AssetId, http.RequestAborted);

            foreach (var w in byChain)
            {
                var inTotal = received.TryGetValue(w.Address, out var r) ? BigInteger.Parse(r, CultureInfo.InvariantCulture) : BigInteger.Zero;
                var outTotal = swept.TryGetValue(w.WalletId, out var s) ? BigInteger.Parse(s.TotalBaseUnits, CultureInfo.InvariantCulture) : BigInteger.Zero;
                recorded[w.WalletId] = (inTotal - outTotal, asset.Decimals, asset.Symbol);
            }
        }

        return items.Select(w =>
        {
            var has = recorded.TryGetValue(w.WalletId, out var rb);
            return new WalletRowView
            {
                WalletId = w.WalletId,
                MerchantId = w.MerchantId,
                MerchantName = w.MerchantId is { } id ? merchantNames.GetValueOrDefault(id) : null,
                Chain = w.Chain,
                Address = w.Address,
                WalletType = w.WalletType,
                Status = w.Status,
                StatusReason = w.StatusReason,
                DepositsReceivedCount = w.DepositsReceivedCount,
                Coin = has ? rb.Coin : null,
                Balance = has ? AmountConversion.ToDisplay(rb.Balance, rb.Decimals) : null,
                BalanceBaseUnits = has ? rb.Balance.ToString(CultureInfo.InvariantCulture) : null,
                CreatedAt = w.CreatedAt,
                UpdatedAt = w.UpdatedAt,
            };
        }).ToList();
    }

    /// <summary>One wallet row — the list shape. A class (not an anonymous type) so the detail view can extend it
    /// and the two can never disagree about a shared field.</summary>
    public class WalletRowView
    {
        public Guid WalletId { get; init; }
        public Guid? MerchantId { get; init; }
        public string? MerchantName { get; init; }
        public Chain Chain { get; init; }
        public string Address { get; init; } = null!;
        public string WalletType { get; init; } = null!;
        public string Status { get; init; } = null!;
        public string? StatusReason { get; init; }
        public int DepositsReceivedCount { get; init; }

        /// <summary>The asset the balance is in (USDT). Null when there is no recorded balance (non-deposit wallet).</summary>
        public string? Coin { get; init; }

        /// <summary>Recorded balance awaiting sweep (received − swept), display decimal. Deposit wallets only.</summary>
        public decimal? Balance { get; init; }

        /// <summary>The same as an exact base-unit integer string (§14).</summary>
        public string? BalanceBaseUnits { get; init; }

        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    /// <summary>The single-wallet detail: the list row plus the live chain reads and the sweep history total.</summary>
    public sealed class WalletDetailView : WalletRowView
    {
        public WalletDetailView() { }

        public WalletDetailView(WalletRowView row)
        {
            WalletId = row.WalletId; MerchantId = row.MerchantId; MerchantName = row.MerchantName; Chain = row.Chain;
            Address = row.Address; WalletType = row.WalletType; Status = row.Status; StatusReason = row.StatusReason;
            DepositsReceivedCount = row.DepositsReceivedCount; Coin = row.Coin; Balance = row.Balance;
            BalanceBaseUnits = row.BalanceBaseUnits; CreatedAt = row.CreatedAt; UpdatedAt = row.UpdatedAt;
        }

        /// <summary>Live USDT on the address right now, read from the chain. Null if the chain couldn't be read.</summary>
        public decimal? OnChainBalance { get; init; }
        public string? OnChainBalanceBaseUnits { get; init; }

        /// <summary>The chain's gas coin (TRX on TRON) and its decimals — never assume them on the client.</summary>
        public string GasSymbol { get; init; } = null!;
        public int GasDecimals { get; init; }

        /// <summary>Live gas-coin balance on the address. Null if the chain couldn't be read.</summary>
        public decimal? GasBalance { get; init; }
        public string? GasBalanceBaseUnits { get; init; }

        /// <summary>Lifetime USDT swept out of this address (confirmed sweeps), and how many sweeps.</summary>
        public decimal? TotalSwept { get; init; }
        public string? TotalSweptBaseUnits { get; init; }
        public int SweepCount { get; init; }
    }

    private static async Task<IResult> SuspendAsync(
        Guid id, SuspendWalletRequest request, IWalletAdminService wallets, IAuditLogger audit, HttpContext http)
    {
        var result = await wallets.SuspendAsync(new SuspendWalletCommand(id, request.Reason), http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "wallet.suspended", "Wallet", id.ToString(),
            request.Reason, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { walletId = id, status = "Suspended" }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> ResumeAsync(
        Guid id, IWalletAdminService wallets, IAuditLogger audit, HttpContext http)
    {
        var result = await wallets.ResumeAsync(new ResumeWalletCommand(id), http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "wallet.resumed", "Wallet", id.ToString(),
            null, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { walletId = id, status = "Active" }, error = (string?)null, errorCode = (string?)null });
    }
}
