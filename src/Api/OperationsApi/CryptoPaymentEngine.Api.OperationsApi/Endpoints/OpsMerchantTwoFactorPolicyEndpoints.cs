using System.ComponentModel.DataAnnotations;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

public sealed class SaveMerchantPlatformTwoFactorPolicyRequest
{
    /// <summary>The complete platform minimum, not a delta — a save replaces the list.</summary>
    public IReadOnlyList<string> GuardedActions { get; init; } = [];

    [MaxLength(512)] public string? Note { get; init; }
}

/// <summary>
/// The PLATFORM MINIMUM for merchant-portal 2FA: which portal actions every merchant must protect with a fresh
/// authenticator code. Set here by platform staff; each merchant sees it locked on their own portal settings
/// page and can only ADD to it (docs: backoffice-frontend-integration.md §3e).
///
/// <para>Saving is guarded by the staff side's always-on <see cref="GuardedActions.TwoFactorPolicy"/>: changing
/// which actions require 2FA — for staff or for merchants — always costs a fresh code.</para>
/// </summary>
public static class OpsMerchantTwoFactorPolicyEndpoints
{
    public static void MapOpsMerchantTwoFactorPolicyApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/merchant-two-factor/actions", GetActions)
            .RequirePermission(OpsPermissions.Roles.View);

        app.MapGet("/api/v1/ops/merchant-two-factor/policy", GetPolicyAsync)
            .RequirePermission(OpsPermissions.Roles.View);

        app.MapGet("/api/v1/ops/merchant-two-factor/policy/history", GetHistoryAsync)
            .RequirePermission(OpsPermissions.Roles.View);

        app.MapPut("/api/v1/ops/merchant-two-factor/policy", SaveAsync)
            .RequirePermission(OpsPermissions.Roles.Manage)
            .RequireTwoFactor(GuardedActions.TwoFactorPolicy);

        app.MapPost("/api/v1/ops/merchant-two-factor/policy/restore-defaults", RestoreDefaultsAsync)
            .RequirePermission(OpsPermissions.Roles.Manage)
            .RequireTwoFactor(GuardedActions.TwoFactorPolicy);

        // Read-only: what one merchant actually enforces (platform minimum + that merchant's own additions).
        app.MapGet("/api/v1/ops/merchants/{id:guid}/two-factor-policy", GetForMerchantAsync)
            .RequirePermission(OpsPermissions.Merchants.View);
    }

    private static IResult GetActions() =>
        OpsResults.Ok(new
        {
            actions = MerchantGuardedActions.Guardable.Select(a => new
            {
                code = a.Code, group = a.Group, label = a.Label, recommended = a.Recommended,
            }),
            // The merchant-side self-protecting action applies to the MERCHANT's own settings page. It is not
            // part of the platform minimum (staff can't untick it and needn't tick it) — listed so the screen
            // can explain it.
            alwaysGuarded = new[]
            {
                new
                {
                    code = MerchantGuardedActions.TwoFactorPolicy,
                    group = "Security",
                    label = "Change which actions require two-factor (merchant's own settings page)",
                    reason = "Always required. If this could be switched off, every other requirement could be too.",
                },
            },
        });

    private static async Task<IResult> GetPolicyAsync(HttpContext http, IMerchantTwoFactorPolicyService policy)
    {
        var view = await policy.GetPlatformAsync(http.RequestAborted);
        return OpsResults.Ok(new
        {
            guardedActions = view.Current.GuardedActions,
            source = view.Current.Source == MerchantTwoFactorPolicySource.Stored ? "Stored" : "Configuration",
            updatedBy = view.Current.UpdatedBy,
            updatedAt = view.Current.UpdatedAt,
            note = view.Current.Note,
            configuredDefaults = view.ConfiguredDefaults,
            recommendedDefaults = view.RecommendedDefaults,
        });
    }

    private static async Task<IResult> GetHistoryAsync(
        HttpContext http, IMerchantTwoFactorPolicyService policy, int limit = 50)
    {
        var history = await policy.GetHistoryAsync(null, limit, http.RequestAborted);
        return OpsResults.Ok(new
        {
            versions = history.Select(v => new
            {
                id = v.Id, guardedActions = v.GuardedActions(), updatedBy = v.UpdatedBy, updatedAt = v.UpdatedAt,
                note = v.Note,
            }),
        });
    }

    private static async Task<IResult> SaveAsync(
        SaveMerchantPlatformTwoFactorPolicyRequest request, HttpContext http,
        IMerchantTwoFactorPolicyService policy, IAuditLogger audit)
    {
        var actor = AuditActor.From(http);
        var before = await policy.GetPlatformAsync(http.RequestAborted);
        var result = await policy.SavePlatformAsync(request.GuardedActions, request.Note, actor.Username, http.RequestAborted);
        return await CompleteAsync(result, before, request.Note, "merchant_two_factor.policy_changed", http, audit);
    }

    private static async Task<IResult> RestoreDefaultsAsync(
        RestoreTwoFactorPolicyRequest? request, HttpContext http, IMerchantTwoFactorPolicyService policy,
        IAuditLogger audit)
    {
        var actor = AuditActor.From(http);
        var before = await policy.GetPlatformAsync(http.RequestAborted);
        var result = await policy.RestorePlatformDefaultsAsync(request?.Note, actor.Username, http.RequestAborted);
        return await CompleteAsync(
            result, before, request?.Note, "merchant_two_factor.policy_restored_defaults", http, audit);
    }

    private static async Task<IResult> CompleteAsync(
        Result<MerchantPlatformTwoFactorPolicy> result, MerchantPlatformTwoFactorPolicyView before, string? note,
        string auditAction, HttpContext http, IAuditLogger audit)
    {
        if (result.IsFailure)
        {
            return result.Error == MerchantTwoFactorPolicyErrors.UnknownAction
                ? OpsResults.Bad(OpsErrorCodes.UnknownGuardedAction, result.Error!.Message)
                : OpsResults.Fail(result.Error!);
        }

        var actor = AuditActor.From(http);
        await audit.LogAsync(
            new LogAuditEntryCommand(
                actor.StaffUserId, actor.Username, auditAction, "MerchantTwoFactorPolicy", "platform",
                Reason: MerchantTwoFactorPolicyAudit.Describe(
                    before.Current.GuardedActions, result.Value.GuardedActions, note),
                actor.IpAddress),
            http.RequestAborted);

        return OpsResults.Ok(new
        {
            guardedActions = result.Value.GuardedActions,
            source = "Stored",
            updatedBy = result.Value.UpdatedBy,
            updatedAt = result.Value.UpdatedAt,
            note = result.Value.Note,
        });
    }

    private static async Task<IResult> GetForMerchantAsync(
        Guid id, HttpContext http, IMerchantTwoFactorPolicyService policy)
    {
        var p = await policy.GetForMerchantAsync(id, http.RequestAborted);
        return OpsResults.Ok(new
        {
            merchantId = id,
            guardedActions = p.GuardedActions,
            platformRequired = p.PlatformRequired,
            merchantAdded = p.MerchantAdded,
            source = p.MerchantSource.ToString(),
            updatedBy = p.UpdatedBy,
            updatedAt = p.UpdatedAt,
            note = p.Note,
        });
    }
}
