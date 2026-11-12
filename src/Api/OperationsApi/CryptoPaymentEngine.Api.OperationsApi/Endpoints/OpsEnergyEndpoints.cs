using System.Globalization;
using System.Numerics;
using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
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
    private static readonly string[] Kinds = ["Stake", "Delegate", "TopUp", "Undelegate", "Unstake", "WithdrawUnstaked"];

    public static void MapOpsEnergyApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/energy/operations", ListOperationsAsync).RequirePermission(OpsPermissions.Energy.View);
        app.MapGet("/api/v1/ops/energy/resources", ListResourcesAsync).RequirePermission(OpsPermissions.Energy.View);

        // The staking (energy) wallet and its rules. Creating the wallet derives an address (and, the first time,
        // seals the energy seed under KMS); setting the rules writes the policy row. Neither stakes, delegates or
        // signs: the money host's workers do that from these settings (§4.7, §10).
        app.MapGet("/api/v1/ops/energy/staking", GetStakingAsync).RequirePermission(OpsPermissions.Energy.View);
        app.MapPost("/api/v1/ops/energy/staking-wallet/provision", ProvisionStakingWalletAsync)
            .RequirePermission(OpsPermissions.Energy.Manage)
            .RequireTwoFactor(GuardedActions.TreasuryProvision);
        app.MapPut("/api/v1/ops/energy/policy", SetPolicyAsync)
            .RequirePermission(OpsPermissions.Energy.Manage)
            .RequireTwoFactor(GuardedActions.EnergySettings);

        // Getting staked TRX back: unstake starts the network's waiting period, withdraw collects what finished it.
        // Both only queue an operation; the money host's worker signs and sends it.
        app.MapPost("/api/v1/ops/energy/unstake", UnstakeAsync)
            .RequirePermission(OpsPermissions.Energy.Manage)
            .RequireTwoFactor(GuardedActions.EnergySettings);
        app.MapPost("/api/v1/ops/energy/withdraw-unstaked", WithdrawUnstakedAsync)
            .RequirePermission(OpsPermissions.Energy.Manage)
            .RequireTwoFactor(GuardedActions.EnergySettings);
    }

    private static async Task<IResult> UnstakeAsync(
        UnstakeRequest request, EnergyAdministrationService energy, IAuditLogger audit, HttpContext http)
    {
        if (!TryParseRequiredChain(request.Chain, out var parsed, out var bad))
            return bad!;

        if (!AmountConversion.TryToBaseUnits(request.Amount, TrxDecimals, out var sun))
            return Bad(OpsErrorCodes.InvalidAmount, "amount must be a positive TRX amount with at most 6 decimal places.");

        var result = await energy.RequestUnstakeAsync(parsed, sun, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await AuditAsync(audit, http, "energy.unstake_requested", "EnergyOperation", result.Value.ToString(),
            $"{parsed} amount={request.Amount} TRX", request.Reason);

        return OpsResults.Ok(new
        {
            operationId = result.Value,
            chain = parsed.ToString(),
            amountTrx = AmountConversion.ToDisplay(sun, TrxDecimals),
            amountSun = sun.ToString(CultureInfo.InvariantCulture),
        });
    }

    private static async Task<IResult> WithdrawUnstakedAsync(
        WithdrawUnstakedRequest request, EnergyAdministrationService energy, IAuditLogger audit, HttpContext http)
    {
        if (!TryParseRequiredChain(request.Chain, out var parsed, out var bad))
            return bad!;

        var result = await energy.RequestWithdrawUnstakedAsync(parsed, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await AuditAsync(audit, http, "energy.withdraw_unstaked_requested", "EnergyOperation", result.Value.ToString(),
            parsed.ToString(), request.Reason);

        return OpsResults.Ok(new { operationId = result.Value, chain = parsed.ToString() });
    }

    /// <summary>The chain's staking wallet (null until created) and its rules (null until saved).</summary>
    private static async Task<IResult> GetStakingAsync(EnergyAdministrationService energy, HttpContext http, string? chain = null)
    {
        if (!TryParseRequiredChain(chain, out var parsed, out var bad))
            return bad!;

        var wallet = await energy.FindStakingWalletAsync(parsed, http.RequestAborted);
        var policy = await energy.GetPolicyAsync(parsed, http.RequestAborted);
        return OpsResults.Ok(new
        {
            chain = parsed.ToString(),
            stakingWallet = wallet is null ? null : new { walletId = wallet.WalletId, address = wallet.Address },
            policy = policy is null ? null : Describe(policy),
        });
    }

    private static async Task<IResult> ProvisionStakingWalletAsync(
        ProvisionStakingWalletRequest request, EnergyAdministrationService energy, IAuditLogger audit, HttpContext http)
    {
        if (!TryParseRequiredChain(request.Chain, out var parsed, out var bad))
            return bad!;

        var result = await energy.ProvisionStakingWalletAsync(parsed, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        if (result.Value.Created)
            await AuditAsync(audit, http, "energy.staking_wallet_created", "StakingWallet", result.Value.WalletId.ToString(),
                $"{parsed} address={result.Value.Address}", request.Reason);

        return OpsResults.Ok(new
        {
            chain = parsed.ToString(),
            walletId = result.Value.WalletId,
            address = result.Value.Address,
            created = result.Value.Created,
        });
    }

    private static async Task<IResult> SetPolicyAsync(
        SetStakingPolicyRequest request, EnergyAdministrationService energy, IAuditLogger audit, HttpContext http)
    {
        if (!TryParseRequiredChain(request.Chain, out var parsed, out var bad))
            return bad!;

        var result = await energy.SetPolicyAsync(new SetStakingPolicyCommand(
            parsed, request.MinimumEnergy, request.TargetEnergy, request.StakeThreshold, request.EnableAutoStake),
            http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await AuditAsync(audit, http, "energy.policy_changed", "EnergyPolicy", parsed.ToString(),
            $"{parsed} min={request.MinimumEnergy} target={request.TargetEnergy} stakeAt={request.StakeThreshold} autoStake={request.EnableAutoStake}",
            request.Reason);

        return OpsResults.Ok(Describe(result.Value));
    }

    private static object Describe(StakingPolicyView policy) => new
    {
        minimumEnergy = policy.MinimumEnergy.ToString(CultureInfo.InvariantCulture),
        targetEnergy = policy.TargetEnergy.ToString(CultureInfo.InvariantCulture),
        stakeThreshold = policy.StakeThreshold.ToString(CultureInfo.InvariantCulture),
        enableAutoStake = policy.EnableAutoStake,
        updatedAt = policy.UpdatedAt,
    };

    private static async Task AuditAsync(
        IAuditLogger audit, HttpContext http, string action, string entityType, string entityId, string detail, string? reason)
    {
        var actor = AuditActor.From(http);
        var text = string.IsNullOrWhiteSpace(reason) ? detail : $"{reason.Trim()} ({detail})";
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, action, entityType, entityId,
            text.Length <= 512 ? text : text[..512], actor.IpAddress), http.RequestAborted);
    }

    private static bool TryParseRequiredChain(string? chain, out Chain parsed, out IResult? bad)
    {
        parsed = default;
        bad = null;
        if (string.IsNullOrWhiteSpace(chain))
        {
            bad = Bad(OpsErrorCodes.NetworkRequired, "A chain is required, e.g. Tron.");
            return false;
        }

        // Enum.TryParse also accepts a bare number such as "7", which would name a chain that does not exist.
        if (int.TryParse(chain, out _) || !Enum.TryParse(chain, ignoreCase: true, out parsed))
        {
            bad = Bad(OpsErrorCodes.InvalidChain, $"Unknown chain '{chain}'.");
            return false;
        }

        return true;
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
            return Bad(OpsErrorCodes.InvalidChain, chainError!);

        string? normalisedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            normalisedKind = Kinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
            if (normalisedKind is null)
                return Bad(OpsErrorCodes.InvalidKind, $"Unknown kind '{kind}'. Expected one of: {string.Join(", ", Kinds)}.");
        }

        string? normalisedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalisedStatus = Statuses.FirstOrDefault(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
            if (normalisedStatus is null)
                return Bad(OpsErrorCodes.InvalidStatus, $"Unknown status '{status}'. Expected one of: {string.Join(", ", Statuses)}.");
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
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static async Task<IResult> ListResourcesAsync(
        IWalletResourceStore store, HttpContext http, string? chain = null)
    {
        if (!TryParseChain(chain, out var chainFilter, out var chainError))
            return Bad(OpsErrorCodes.InvalidChain, chainError!);

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

        return Results.Ok(new { isSuccess = true, data = new { items = ordered }, error = (string?)null, errorCode = (string?)null });
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

    private static IResult Bad(string errorCode, string message) => OpsResults.Bad(errorCode, message);
}
