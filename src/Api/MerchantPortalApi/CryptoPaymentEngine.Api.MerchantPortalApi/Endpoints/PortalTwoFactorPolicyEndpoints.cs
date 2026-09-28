using System.ComponentModel.DataAnnotations;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Domain;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

public sealed class SavePortalTwoFactorPolicyRequest
{
    /// <summary>Every action the merchant wants guarded. May include the platform-required (locked) ones — they
    /// are ignored for storage, since the merchant cannot change them either way.</summary>
    public IReadOnlyList<string> GuardedActions { get; init; } = [];

    [MaxLength(512)] public string? Note { get; init; }
}

public sealed class RestorePortalTwoFactorPolicyRequest
{
    [MaxLength(512)] public string? Note { get; init; }
}

/// <summary>
/// The merchant's own "2FA 验证" settings page: which portal actions demand a fresh authenticator code.
///
/// <para><b>Two layers.</b> The platform minimum is set by platform staff in the admin back office; it is shown
/// here LOCKED and the merchant cannot remove any of it. On top, a merchant admin may switch on MORE actions for
/// their own team. Tenant-scoped like every portal route: the merchant id comes only from the session.</para>
///
/// <para>Saving is always guarded (<see cref="MerchantGuardedActions.TwoFactorPolicy"/>) — otherwise a stolen
/// merchant-admin session could quietly remove every extra protection the merchant chose.</para>
/// </summary>
public static class PortalTwoFactorPolicyEndpoints
{
    public static void MapPortalTwoFactorPolicyApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/portal/two-factor/actions", GetActions)
            .RequirePortalPermission(PortalPermissions.Roles.View);

        app.MapGet("/api/v1/portal/two-factor/policy", GetPolicyAsync)
            .RequirePortalPermission(PortalPermissions.Roles.View);

        app.MapGet("/api/v1/portal/two-factor/policy/history", GetHistoryAsync)
            .RequirePortalPermission(PortalPermissions.Roles.View);

        app.MapPut("/api/v1/portal/two-factor/policy", SaveAsync)
            .RequirePortalPermission(PortalPermissions.Roles.Manage)
            .RequirePortalTwoFactor(MerchantGuardedActions.TwoFactorPolicy);

        app.MapPost("/api/v1/portal/two-factor/policy/restore-defaults", RestoreDefaultsAsync)
            .RequirePortalPermission(PortalPermissions.Roles.Manage)
            .RequirePortalTwoFactor(MerchantGuardedActions.TwoFactorPolicy);
    }

    private static IResult GetActions() => PortalResults.Ok(ActionsPayload());

    /// <summary>The catalog as both settings screens render it. Shared with nothing else on purpose — the
    /// admin back office builds the same shape from the same catalog.</summary>
    internal static object ActionsPayload() => new
    {
        actions = MerchantGuardedActions.Guardable.Select(a => new
        {
            code = a.Code, group = a.Group, label = a.Label, recommended = a.Recommended,
        }),
        alwaysGuarded = new[]
        {
            new
            {
                code = MerchantGuardedActions.TwoFactorPolicy,
                group = "Security",
                label = "Change which actions require two-factor",
                reason = "Always required. If this could be switched off, every other requirement could be too.",
            },
        },
    };

    private static async Task<IResult> GetPolicyAsync(HttpContext http, IMerchantTwoFactorPolicyService policy) =>
        PortalResults.Ok(ToPayload(await policy.GetForMerchantAsync(PortalTenant.MerchantId(http), http.RequestAborted)));

    private static async Task<IResult> GetHistoryAsync(
        HttpContext http, IMerchantTwoFactorPolicyService policy, int limit = 50)
    {
        var history = await policy.GetHistoryAsync(PortalTenant.MerchantId(http), limit, http.RequestAborted);
        return PortalResults.Ok(new
        {
            // Only THIS merchant's own changes. Platform-minimum changes are made by platform staff and are not
            // the merchant's history to read.
            versions = history.Select(v => new
            {
                id = v.Id,
                merchantAdded = v.GuardedActions(),
                updatedBy = v.UpdatedBy,
                updatedAt = v.UpdatedAt,
                note = v.Note,
            }),
        });
    }

    private static async Task<IResult> SaveAsync(
        SavePortalTwoFactorPolicyRequest request, HttpContext http, IMerchantTwoFactorPolicyService policy,
        IAuditLogger audit)
    {
        var principal = PortalTenant.Principal(http);
        var before = await policy.GetForMerchantAsync(principal.MerchantId, http.RequestAborted);

        var result = await policy.SaveForMerchantAsync(
            principal.MerchantId, request.GuardedActions, request.Note, principal.Username, http.RequestAborted);

        return await CompleteAsync(result, before, request.Note, PortalAuditActions.TwoFactorPolicyChanged, http, audit);
    }

    private static async Task<IResult> RestoreDefaultsAsync(
        RestorePortalTwoFactorPolicyRequest? request, HttpContext http, IMerchantTwoFactorPolicyService policy,
        IAuditLogger audit)
    {
        var principal = PortalTenant.Principal(http);
        var before = await policy.GetForMerchantAsync(principal.MerchantId, http.RequestAborted);

        var result = await policy.RestoreMerchantDefaultsAsync(
            principal.MerchantId, request?.Note, principal.Username, http.RequestAborted);

        return await CompleteAsync(
            result, before, request?.Note, PortalAuditActions.TwoFactorPolicyRestoredDefaults, http, audit);
    }

    private static async Task<IResult> CompleteAsync(
        CryptoPaymentEngine.SharedKernel.Result<MerchantTwoFactorPolicy> result, MerchantTwoFactorPolicy before,
        string? note, string auditAction, HttpContext http, IAuditLogger audit)
    {
        if (result.IsFailure)
        {
            return result.Error == MerchantTwoFactorPolicyErrors.UnknownAction
                ? PortalResults.Bad(PortalErrorCodes.UnknownGuardedAction, result.Error!.Message)
                : PortalResults.Fail(result.Error!);
        }

        var principal = PortalTenant.Principal(http);
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                auditAction, PortalAuditActions.EntityTwoFactorPolicy, principal.MerchantId.ToString(),
                MerchantTwoFactorPolicyAudit.Describe(before.GuardedActions, result.Value.GuardedActions, note)),
            http.RequestAborted);

        return PortalResults.Ok(ToPayload(result.Value));
    }

    private static object ToPayload(MerchantTwoFactorPolicy p) => new
    {
        guardedActions = p.GuardedActions,
        platformRequired = p.PlatformRequired,
        merchantAdded = p.MerchantAdded,
        source = p.MerchantSource.ToString(),
        updatedBy = p.UpdatedBy,
        updatedAt = p.UpdatedAt,
        note = p.Note,
    };
}
