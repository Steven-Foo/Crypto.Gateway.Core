using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Domain;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// The staff landing page's aggregate read (REQ-3) — a <b>work queue</b> first and a set of KPI tiles second.
///
/// <para><b>Pure composition, no new truth.</b> Every number here is read through a module Contract that
/// already backs a list endpoint, so a tile and the screen it links to are guaranteed to agree — a dashboard
/// that computed its own counts would be the classic "the tile says 3, the list shows 5" bug. It moves no
/// money, writes nothing, and runs no worker (§4.7).</para>
///
/// <para><b>Cost.</b> The operational block is grouped SQL COUNTs plus two already-derived snapshot reads
/// (Mongo), not table scans of the transaction history — this is a landing page and must stay cheap.</para>
///
/// <para><b>Authenticated staff only, no extra permission</b> — it exposes nothing a staff member cannot
/// already read, and gating the landing page behind a permission would leave a fresh account with a blank
/// screen and no way to tell why.</para>
/// </summary>
public static class OpsDashboardEndpoints
{
    public static void MapOpsDashboardApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/dashboard", GetAsync);

    private static async Task<IResult> GetAsync(
        IWithdrawalDirectory withdrawals,
        ICallbackDeliveryQuery callbacks,
        IWalletAdminService wallets,
        IReconciliationStore reconciliation,
        IWalletResourceStore resources,
        IAssetCatalog assets,
        TimeProvider clock,
        ILogger<Program> logger,
        HttpContext http)
    {
        var ct = http.RequestAborted;

        // ── operational: the work queue ──
        var withdrawalCounts = await withdrawals.GetStatusCountsAsync(ct);
        var callbackCounts = await callbacks.GetStatusCountsAsync(ct);

        // A COUNT, not a page of rows: pageSize 1 and we read only the total.
        var (_, suspendedWallets) = await wallets.SearchAsync(
            new WalletAdminFilter(null, null, null, WalletStatus.Suspended), 1, 1, ct);

        // The two Mongo-backed blocks below are DERIVED observability data (§2), never money truth — so a
        // Mongo outage must not take down the landing page and hide the SQL-backed work queue above, which is
        // the operator's actual to-do list and is perfectly healthy. Degrade to "unavailable" instead, and say
        // so in the payload rather than silently reporting zero (a fake 0 drift reads as "all balanced",
        // which on a custody figure is the worst possible thing to show).
        var snapshots = await TryReadAsync(() => reconciliation.ListAsync(ct), logger, "reconciliation snapshots");
        var resourceSnapshots = await TryReadAsync(() => resources.ListAsync(ct), logger, "wallet resource snapshots");

        var operational = new
        {
            // Keys come from the shared effective-status vocabulary, so these tiles link straight to
            // /transactions/withdrawals?status=<same value>.
            withdrawalsPendingMerchantApproval = withdrawalCounts.GetValueOrDefault("pending_merchant_approval"),
            withdrawalsPendingApproval = withdrawalCounts.GetValueOrDefault("pending_approval"),
            withdrawalsAwaitingFunds = withdrawalCounts.GetValueOrDefault("insufficient_balance"),
            withdrawalsAwaitingRelease = withdrawalCounts.GetValueOrDefault("awaiting_release"),
            // The two merchant-settlement queues. Both need a human — an admin to audit, then finance to pay
            // externally — so leaving them off the landing page would hide exactly the work it exists to show.
            settlementsPendingAudit = withdrawalCounts.GetValueOrDefault("pending_admin_audit"),
            settlementsPendingFinanceTransfer = withdrawalCounts.GetValueOrDefault("pending_finance_transfer"),
            callbacksAbandoned = callbackCounts.GetValueOrDefault("Abandoned"),
            callbacksPending = callbackCounts.GetValueOrDefault("PendingNotification"),
            walletsSuspended = suspendedWallets,
            // Drift AND Incomplete both need an operator: an Incomplete run could not read every address, so
            // it is an unknown, not a clean bill of health — counting only Drift would under-report risk.
            // Null (not 0) when the snapshot store is unreachable: "unknown" and "none" must look different.
            reconciliationDriftCount = snapshots?.Count(s => s.Status == ReconciliationStatus.Drift),
            reconciliationIncompleteCount = snapshots?.Count(s => s.Status == ReconciliationStatus.Incomplete),
            energyWalletsCritical = resourceSnapshots?.Count(r => r.Health == ResourceHealth.Critical),
            energyWalletsLow = resourceSnapshots?.Count(r => r.Health == ResourceHealth.Low),
        };

        // ── custody: the same snapshots /ops/reconciliation serves, summarised per asset ──
        var decimalsByAsset = new Dictionary<Guid, int>();
        var custody = new List<object>(snapshots?.Count ?? 0);
        foreach (var s in (snapshots ?? []).OrderBy(s => s.Chain).ThenBy(s => s.AssetSymbol))
        {
            if (!decimalsByAsset.TryGetValue(s.AssetId, out var decimals))
            {
                var asset = await assets.FindByIdAsync(s.AssetId, ct);
                decimals = asset?.Decimals ?? 6;
                decimalsByAsset[s.AssetId] = decimals;
            }

            custody.Add(new
            {
                chain = s.Chain.ToString(),
                assetId = s.AssetId,
                coin = s.AssetSymbol,
                status = s.Status.ToString(),
                ledgerTreasuryHolding = AmountConversion.ToDisplay(s.LedgerHolding, decimals),
                onChainTotal = AmountConversion.ToDisplay(s.OnChainTotal, decimals),
                // Drift stays an exact base-unit integer as well as a display decimal — a custody audit needs
                // the precise integer (§14), consistent with /ops/reconciliation.
                drift = AmountConversion.ToDisplay(s.Drift, decimals),
                driftBaseUnits = s.Drift.ToString(),
                decimals,
                observedAt = s.ObservedAt,
            });
        }

        return OpsResults.Ok(new
        {
            generatedAt = clock.GetUtcNow(),
            operational,
            custody,
            // Tells the UI whether the Mongo-derived blocks above are real or missing, so it can show
            // "unavailable" on those tiles instead of rendering a null as a zero.
            custodyAvailable = snapshots is not null,
            energyHealthAvailable = resourceSnapshots is not null,
            // `volume` (per-asset deposit/withdrawal counts and sums over a time window) is NOT served here.
            // The existing totals path folds BigInteger money client-side by design (there is no SQL SUM
            // translation for this project's money mapping, §14), so a 30-day per-asset breakdown would load
            // every row in the window — a table scan on the landing page, which is the one thing REQ-3's
            // acceptance criteria rule out. It needs a purpose-built grouped aggregate; filed as a follow-up.
            volume = Array.Empty<object>(),
        });
    }

    /// <summary>
    /// Reads one optional, derived (Mongo) block, returning null instead of throwing if the store is
    /// unreachable. Deliberately scoped to the dashboard: the dedicated <c>/ops/reconciliation</c> screen
    /// still fails loudly, because there "the custody audit is down" IS the answer to the question being
    /// asked — whereas here it is one tile on a page whose other numbers are fine.
    /// </summary>
    private static async Task<IReadOnlyList<T>?> TryReadAsync<T>(
        Func<Task<IReadOnlyList<T>>> read, ILogger logger, string what)
    {
        try
        {
            return await read();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dashboard could not read {What}; serving the page without that block.", what);
            return null;
        }
    }
}
