using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Sweep.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing sweep screens: the state of the concentration path (deposit address → cold collection
/// wallet), the per-chain dials behind it, and a manual "scan now".
///
/// <para>Reads and settings only — this host runs no scan/sign/broadcast worker and holds no keys (§4.7).
/// The manual trigger is therefore a <b>request</b>: it stamps the chain's settings row and the money host
/// picks it up on its next tick, rather than this host reaching for a node itself.</para>
///
/// <para>Amounts are shown as a display value plus the exact base-unit integer (§14).</para>
/// </summary>
public static class OpsSweepEndpoints
{
    private static readonly string[] Statuses = ["Pending", "Signing", "Broadcast", "Confirmed", "Failed"];

    public static void MapOpsSweepApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/sweeps", ListAsync).RequirePermission(OpsPermissions.Sweep.View);

        // Kept at its original path for the screens already built against it; it now reports the EFFECTIVE
        // settings (configuration as the floor, a stored row as the operator's override) rather than this
        // host's raw configuration, which could drift from what the money host actually sweeps against.
        app.MapGet("/api/v1/ops/sweeps/policies", SettingsAsync).RequirePermission(OpsPermissions.Sweep.View);
        app.MapGet("/api/v1/ops/sweeps/settings", SettingsAsync).RequirePermission(OpsPermissions.Sweep.View);

        app.MapPut("/api/v1/ops/sweeps/settings/{chain}", UpdateSettingsAsync)
            .RequirePermission(OpsPermissions.Sweep.Manage);
        app.MapPost("/api/v1/ops/sweeps/scan/{chain}", RequestScanAsync)
            .RequirePermission(OpsPermissions.Sweep.Manage);
    }

    /// <summary>
    /// The effective sweep dials per chain, plus when each chain last ran and whether a manual pass is
    /// pending. The threshold is one base-unit integer applied to every active asset on the chain, so it is
    /// shown converted per asset rather than as one guessed display amount (§14). A chain with no configured
    /// policy is reported as not configured rather than given an invented default.
    /// </summary>
    private static async Task<IResult> SettingsAsync(
        ISweepSettingsService settings, IAssetCatalog assets, HttpContext http)
    {
        var configured = (await settings.ListAsync(http.RequestAborted))
            .ToDictionary(s => s.Chain, StringComparer.OrdinalIgnoreCase);
        var active = await assets.GetActiveAsync(http.RequestAborted);

        var rows = Enum.GetValues<Chain>().Select(chain =>
        {
            configured.TryGetValue(chain.ToString(), out var row);
            var threshold = row is null ? (BigInteger?)null : BigInteger.Parse(row.MinSweepAmountBaseUnits, CultureInfo.InvariantCulture);

            return new
            {
                chain = chain.ToString(),
                configured = row is not null,
                enabled = row?.Enabled,
                minSweepAmountBaseUnits = row?.MinSweepAmountBaseUnits,
                confirmations = row?.Confirmations,
                scanIntervalMinutes = row?.ScanIntervalMinutes,
                source = row?.Source,
                scanRequestedAt = row?.ScanRequestedAt,
                lastScanStartedAt = row?.LastScanStartedAt,
                lastScanCompletedAt = row?.LastScanCompletedAt,
                lastSweepsCreated = row?.LastSweepsCreated,
                updatedBy = row?.UpdatedBy,
                updatedAt = row?.UpdatedAt,
                assets = active
                    .Where(a => a.Chain == chain)
                    .Select(a => new
                    {
                        coin = a.Symbol,
                        decimals = a.Decimals,
                        minSweepAmount = threshold is null ? (decimal?)null : AmountConversion.ToDisplay(threshold.Value, a.Decimals),
                    })
                    .ToList(),
            };
        });

        return OpsResults.Ok(rows);
    }

    /// <summary>
    /// Re-tunes one chain's dials. Every field is sent together: a partial update on a settings screen
    /// invites changing one dial and silently reverting another to whatever stale value the caller held.
    /// Audited — the threshold decides when customer funds move, and the interval decides how promptly.
    /// </summary>
    private static async Task<IResult> UpdateSettingsAsync(
        string chain,
        UpdateSweepSettingsRequest request,
        ISweepSettingsService settings,
        IAuditLogger audit,
        HttpContext http)
    {
        if (!TryChain(chain, out var parsed))
            return BadChain(chain);

        if (string.IsNullOrWhiteSpace(request.MinSweepAmountBaseUnits))
            return OpsResults.Bad(OpsErrorCodes.InvalidAmount, "A sweep threshold in base units is required.");

        if (!BigInteger.TryParse(
                request.MinSweepAmountBaseUnits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold)
            || threshold < BigInteger.Zero)
            return OpsResults.Bad(
                OpsErrorCodes.InvalidAmount, "The sweep threshold must be a non-negative whole number of base units.");

        var actor = AuditActor.From(http);
        var result = await settings.UpdateAsync(
            parsed,
            new SweepSettingsUpdate(
                request.Enabled, threshold.ToString(CultureInfo.InvariantCulture),
                request.Confirmations, request.ScanIntervalMinutes),
            actor.Username,
            http.RequestAborted);

        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "sweep.settings_updated", "SweepSettings", parsed.ToString(),
            $"enabled={request.Enabled}; threshold={threshold}; confirmations={request.Confirmations}; "
            + $"intervalMinutes={request.ScanIntervalMinutes}",
            actor.IpAddress), http.RequestAborted);

        return OpsResults.Ok(result.Value);
    }

    /// <summary>
    /// Asks for a scan outside the schedule. It returns once the request is recorded — the pass itself runs
    /// on the money host, which claims it on its next tick and consumes the request exactly once however
    /// many instances are polling. Refused for a paused chain, so "paused" means paused.
    /// </summary>
    private static async Task<IResult> RequestScanAsync(
        string chain, ISweepSettingsService settings, IAuditLogger audit, HttpContext http)
    {
        if (!TryChain(chain, out var parsed))
            return BadChain(chain);

        var actor = AuditActor.From(http);
        var result = await settings.RequestScanAsync(parsed, actor.Username, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "sweep.scan_requested", "SweepSettings", parsed.ToString(),
            null, actor.IpAddress), http.RequestAborted);

        return OpsResults.Ok(new
        {
            chain = parsed.ToString(),
            requested = true,
            scanRequestedAt = result.Value.ScanRequestedAt,
            scanIntervalMinutes = result.Value.ScanIntervalMinutes,
        });
    }

    private static async Task<IResult> ListAsync(
        ISweepDirectory sweeps,
        IAssetCatalog assets,
        HttpContext http,
        string? chain = null,
        string? status = null,
        Guid? walletId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        Chain? chainFilter = null;
        if (!string.IsNullOrWhiteSpace(chain))
        {
            if (!TryChain(chain, out var parsed))
                return BadChain(chain);
            chainFilter = parsed;
        }

        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = Statuses.FirstOrDefault(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
            if (normalisedStatus is null)
                return Bad(OpsErrorCodes.InvalidStatus, $"Unknown status '{status}'. Expected one of: {string.Join(", ", Statuses)}.");
        }

        var filter = new SweepAdminFilter(chainFilter, normalisedStatus, walletId, AssetId: null, fromDate, toDate);
        var (items, total) = await sweeps.SearchAsync(filter, page, pageSize, http.RequestAborted);
        var summary = await sweeps.GetStatusCountsAsync(chainFilter, http.RequestAborted);

        // Caches the ASSET rather than just its precision: the row needs the symbol too, and a
        // second lookup per row to fetch it would undo the point of caching at all. Same shape as
        // the deposit endpoint's `assetCache`.
        var assetCache = new Dictionary<Guid, AssetDto?>();
        var rows = new List<object>(items.Count);
        foreach (var s in items)
        {
            if (!assetCache.TryGetValue(s.AssetId, out var asset))
            {
                asset = await assets.FindByIdAsync(s.AssetId, http.RequestAborted);
                assetCache[s.AssetId] = asset;
            }

            var decimals = asset?.Decimals ?? 6;

            rows.Add(new
            {
                sweepId = s.SweepId,
                walletId = s.WalletId,
                chain = s.Chain,
                assetId = s.AssetId,
                // REQ-25. Both were already resolved here to convert the amount below, and neither
                // was sent — leaving a consumer with a bare GUID and no way to put asset context on
                // a figure. The same gap REQ-12 closed on the ledger rows.
                //
                // `coin` is null when the catalog cannot resolve the asset, exactly as deposit rows
                // behave. A null symbol is honest; a guessed one is not.
                coin = asset?.Symbol,
                // The precision ACTUALLY used for the conversion below, fallback included — so
                // `amount` and `amountBaseUnits` stay reconcilable by the caller.
                decimals,
                fromAddress = s.FromAddress,
                toAddress = s.ToAddress,
                amount = AmountConversion.ToDisplay(BigInteger.Parse(s.AmountBaseUnits), decimals),
                amountBaseUnits = s.AmountBaseUnits,
                status = s.Status,
                // Why this sweep went where it went: the collection wallet's class, and the verdict that
                // chose it. A row into the quarantine wallet is the one an operator most wants to open.
                destinationKind = s.DestinationKind,
                screeningDecision = s.ScreeningDecision,
                screeningId = s.ScreeningId,
                txHash = s.TransactionHash,
                confirmations = s.Confirmations,
                failureReason = s.FailureReason,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt,
            });
        }

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, summary, items = rows },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static bool TryChain(string chain, out Chain parsed) => Enum.TryParse(chain, ignoreCase: true, out parsed);

    private static IResult BadChain(string chain) => Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{chain}'.");

    private static IResult Bad(string errorCode, string message) => OpsResults.Bad(errorCode, message);
}
