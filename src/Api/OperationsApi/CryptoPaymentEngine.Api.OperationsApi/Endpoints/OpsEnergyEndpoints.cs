using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Energy.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing TRON energy read — the back-office view of the gas hub. Two screens, both pure reads
/// (§4.7 — this host runs no monitor/stake/delegate worker and holds no keys): the stake/delegate/top-up
/// <b>operation</b> state machine (SQL), and per-wallet <b>resource-health</b> snapshots the money host's
/// monitor writes to Mongo (§2 — derived, never money truth). TRX amounts show as a display value plus exact
/// sun (§14); energy/bandwidth are exact whole-unit integers. Authenticated staff with <c>ops.energy.view</c>.
/// </summary>
public static class OpsEnergyEndpoints
{
    /// <summary>TRX has 6 decimals (sun = 1e-6 TRX). It is deliberately NOT in the deposit asset catalog
    /// (native TRX detection is off), so the divisor is a documented constant here, not a catalog lookup.</summary>
    private const int TrxDecimals = 6;

    private static readonly string[] Statuses = ["Pending", "Signing", "Broadcast", "Confirmed", "Failed"];
    private static readonly string[] Kinds = ["Stake", "Delegate", "TopUp"];

    public static void MapOpsEnergyApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/energy/operations", ListOperationsAsync).RequirePermission(OpsPermissions.Energy.View);
        app.MapGet("/api/v1/ops/energy/resources", ListResourcesAsync).RequirePermission(OpsPermissions.Energy.View);
    }

    private static async Task<IResult> ListOperationsAsync(
        IEnergyOperationDirectory operations,
        HttpContext http,
        string? chain = null,
        string? kind = null,
        string? status = null,
        Guid? stakingWalletId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        if (!TryParseChain(chain, out var chainFilter, out var chainError))
            return Bad(chainError!);

        string? normalisedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            normalisedKind = Kinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
            if (normalisedKind is null)
                return Bad($"Unknown kind '{kind}'. Expected one of: {string.Join(", ", Kinds)}.");
        }

        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = Statuses.FirstOrDefault(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
            if (normalisedStatus is null)
                return Bad($"Unknown status '{status}'. Expected one of: {string.Join(", ", Statuses)}.");
        }

        var filter = new EnergyOperationAdminFilter(
            chainFilter, normalisedKind, normalisedStatus, stakingWalletId, fromDate, toDate);
        var (items, total) = await operations.SearchAsync(filter, page, pageSize, http.RequestAborted);
        var summary = await operations.GetStatusCountsAsync(chainFilter, http.RequestAborted);

        var rows = items.Select(o => new
        {
            operationId = o.OperationId,
            kind = o.Kind,
            chain = o.Chain,
            stakingWalletId = o.StakingWalletId,
            ownerAddress = o.OwnerAddress,
            targetAddress = o.TargetAddress,
            amountTrx = AmountConversion.ToDisplay(BigInteger.Parse(o.AmountSunBaseUnits, CultureInfo.InvariantCulture), TrxDecimals),
            amountSunBaseUnits = o.AmountSunBaseUnits,
            status = o.Status,
            txHash = o.TransactionHash,
            confirmations = o.Confirmations,
            failureReason = o.FailureReason,
            createdAt = o.CreatedAt,
            updatedAt = o.UpdatedAt,
        }).ToList();

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, summary, items = rows },
            error = (string?)null,
        });
    }

    private static async Task<IResult> ListResourcesAsync(
        IWalletResourceStore store, HttpContext http, string? chain = null)
    {
        if (!TryParseChain(chain, out var chainFilter, out var chainError))
            return Bad(chainError!);

        var snapshots = await store.ListAsync(http.RequestAborted);

        var ordered = snapshots
            .Where(s => chainFilter is not { } c || s.Chain == c)
            // Worst health first (Critical → Low → Healthy) so an operator sees problems at the top.
            .OrderByDescending(s => s.Health)
            .ThenBy(s => s.Chain)
            .ThenBy(s => s.WalletType)
            .Select(s => new
            {
                walletId = s.WalletId,
                chain = s.Chain.ToString(),
                address = s.Address,
                walletType = s.WalletType,
                health = s.Health.ToString(),
                energyAvailable = s.EnergyAvailable.ToString(CultureInfo.InvariantCulture),
                energyLimit = s.EnergyLimit.ToString(CultureInfo.InvariantCulture),
                energyUsed = s.EnergyUsed.ToString(CultureInfo.InvariantCulture),
                bandwidthAvailable = s.BandwidthAvailable.ToString(CultureInfo.InvariantCulture),
                delegatedEnergyOut = s.DelegatedEnergyOut.ToString(CultureInfo.InvariantCulture),
                delegatedEnergyIn = s.DelegatedEnergyIn.ToString(CultureInfo.InvariantCulture),
                frozenTrxForEnergy = AmountConversion.ToDisplay(s.FrozenTrxForEnergy, TrxDecimals),
                frozenTrxForEnergySun = s.FrozenTrxForEnergy.ToString(CultureInfo.InvariantCulture),
                frozenTrxForBandwidth = AmountConversion.ToDisplay(s.FrozenTrxForBandwidth, TrxDecimals),
                frozenTrxForBandwidthSun = s.FrozenTrxForBandwidth.ToString(CultureInfo.InvariantCulture),
                availableTrxBalance = AmountConversion.ToDisplay(s.AvailableTrxBalance, TrxDecimals),
                availableTrxBalanceSun = s.AvailableTrxBalance.ToString(CultureInfo.InvariantCulture),
                targetEnergy = s.TargetEnergy?.ToString(CultureInfo.InvariantCulture),
                minimumEnergy = s.MinimumEnergy?.ToString(CultureInfo.InvariantCulture),
                observedAt = s.ObservedAt,
            })
            .ToList();

        return Results.Ok(new { isSuccess = true, data = new { items = ordered }, error = (string?)null });
    }

    private static bool TryParseChain(string? chain, out Chain? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (string.IsNullOrWhiteSpace(chain))
            return true;
        if (!Enum.TryParse<Chain>(chain, ignoreCase: true, out var c))
        {
            error = $"Unknown chain '{chain}'.";
            return false;
        }
        parsed = c;
        return true;
    }

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
