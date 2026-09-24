using System.ComponentModel.DataAnnotations;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

public sealed class TwoFactorCodeRequest
{
    [Required, MaxLength(32)] public string Code { get; init; } = null!;
}

public sealed class SaveTwoFactorPolicyRequest
{
    /// <summary>The complete guarded set, not a delta — a save replaces the list. A delta API would make
    /// "which actions are guarded right now" depend on replaying history, which is the opposite of what a
    /// settings screen shows.</summary>
    public IReadOnlyList<string> GuardedActions { get; init; } = [];

    [MaxLength(512)] public string? Note { get; init; }
}

/// <summary>
/// Enrollment, recovery codes, and the platform-wide policy saying which actions demand a code.
///
/// <para>The enrollment routes are authenticated but NOT permission-gated and NOT guarded: they are
/// self-scoped (they act on the caller's own account, taken from the validated session and never from a
/// request parameter), and gating them would deadlock a platform where nobody has enrolled yet.</para>
/// </summary>
public static class OpsTwoFactorEndpoints
{
    public static void MapOpsTwoFactorApi(this IEndpointRouteBuilder app)
    {
        // ── the caller's own factor ──
        app.MapGet("/api/v1/ops/auth/2fa/status", GetStatusAsync);
        app.MapPost("/api/v1/ops/auth/2fa/enroll", BeginEnrollmentAsync);
        app.MapPost("/api/v1/ops/auth/2fa/enroll/confirm", ConfirmEnrollmentAsync);

        // Regenerating invalidates the printed list, so it ALWAYS costs a fresh code — verified inside the
        // handler rather than through the policy filter. Two reasons: it is self-scoped, so it carries no
        // permission for the filter to sit behind; and it must not become optional just because an admin
        // untick something unrelated in the settings screen. Replacing a credential always proves identity.
        app.MapPost("/api/v1/ops/auth/2fa/recovery-codes", RegenerateRecoveryCodesAsync);

        // ── administering someone else's ──
        // Account recovery is the standard way around a second factor: if an admin can clear a colleague's
        // enrollment without proving themselves, that is the way through.
        app.MapPost("/api/v1/ops/accounts/{id:guid}/2fa/reset", ResetAsync)
            .RequirePermission(OpsPermissions.Accounts.Manage)
            .RequireTwoFactor(GuardedActions.AccountsManage);

        // ── the policy ──
        app.MapGet("/api/v1/ops/two-factor/actions", GetActions)
            .RequirePermission(OpsPermissions.Roles.View);

        app.MapGet("/api/v1/ops/two-factor/policy", GetPolicyAsync)
            .RequirePermission(OpsPermissions.Roles.View);

        app.MapGet("/api/v1/ops/two-factor/policy/history", GetPolicyHistoryAsync)
            .RequirePermission(OpsPermissions.Roles.View);

        // Always guarded, whatever the saved policy says (§ GuardedActions.TwoFactorPolicy).
        app.MapPut("/api/v1/ops/two-factor/policy", SavePolicyAsync)
            .RequirePermission(OpsPermissions.Roles.Manage)
            .RequireTwoFactor(GuardedActions.TwoFactorPolicy);
    }

    private static async Task<IResult> GetStatusAsync(HttpContext http, ITwoFactorService twoFactor)
    {
        var actor = AuditActor.From(http);
        var status = await twoFactor.GetStatusAsync(actor.StaffUserId, http.RequestAborted);

        return OpsResults.Ok(new
        {
            enrolled = status.Enrolled,
            status = status.Status?.ToString(),
            enrolledAt = status.EnrolledAt,
            recoveryCodesRemaining = status.RecoveryCodesRemaining,
            lockedOut = status.LockedOut,
        });
    }

    /// <summary>
    /// Starts enrollment and returns the provisioning URI ONCE. There is no endpoint that returns it again:
    /// losing the setup means starting over, which is the correct cost for a secret that must not be
    /// re-retrievable by anyone who gets hold of a session later.
    /// </summary>
    private static async Task<IResult> BeginEnrollmentAsync(
        HttpContext http, ITwoFactorService twoFactor, IAuditLogger audit)
    {
        var actor = AuditActor.From(http);

        var result = await twoFactor.BeginEnrollmentAsync(actor.StaffUserId, actor.Username, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username,
                "two_factor.enrollment_started", "StaffUser", actor.StaffUserId.ToString(),
                Reason: "Two-factor enrollment started.", actor.IpAddress),
                http.RequestAborted);

        return OpsResults.Ok(new
        {
            // The QR payload. The client renders it locally — it is never sent anywhere else.
            provisioningUri = result.Value.ProvisioningUri,
            // For manual entry when a camera is unavailable.
            secret = result.Value.SecretBase32,
        });
    }

    private static async Task<IResult> ConfirmEnrollmentAsync(
        TwoFactorCodeRequest request,
        HttpContext http,
        ITwoFactorService twoFactor,
        IStaffAuthService auth,
        IAuditLogger audit,
        Microsoft.Extensions.Options.IOptions<OpsSessionCookieOptions> cookieOptions)
    {
        var actor = AuditActor.From(http);

        var result = await twoFactor.ConfirmEnrollmentAsync(actor.StaffUserId, request.Code, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        // Upgrade the CURRENT session in place, so the user lands on the dashboard rather than back at a
        // login form the instant their setup succeeded.
        OpsSessionCookie.TryReadToken(http, cookieOptions.Value.Name, out var token, out _);
        await auth.CompleteEnrollmentForSessionAsync(token, http.RequestAborted);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username,
                "two_factor.enrolled", "StaffUser", actor.StaffUserId.ToString(),
                Reason: "Two-factor enrollment confirmed.", actor.IpAddress),
                http.RequestAborted);

        return OpsResults.Ok(new
        {
            enrolled = true,
            // Shown once. Nothing returns them again.
            recoveryCodes = result.Value,
        });
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        TwoFactorCodeRequest request, HttpContext http, ITwoFactorService twoFactor, IAuditLogger audit)
    {
        var actor = AuditActor.From(http);

        // Unconditional, not policy-driven (see the route registration).
        var verified = await twoFactor.VerifyAsync(actor.StaffUserId, request.Code, http.RequestAborted);
        if (verified.IsFailure)
            return OpsResults.Fail(verified.Error!);

        var result = await twoFactor.RegenerateRecoveryCodesAsync(actor.StaffUserId, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username,
                "two_factor.recovery_codes_regenerated", "StaffUser", actor.StaffUserId.ToString(),
                Reason: "Recovery codes regenerated; the previous set was invalidated.", actor.IpAddress),
                http.RequestAborted);

        return OpsResults.Ok(new { recoveryCodes = result.Value });
    }

    private static async Task<IResult> ResetAsync(
        Guid id, HttpContext http, ITwoFactorService twoFactor, IStaffAccountService accounts, IAuditLogger audit)
    {
        // Confirm the account exists before reporting success, so a typo'd id is a 404 rather than a silent
        // "reset" of nothing that an admin would read as done.
        var found = await accounts.GetAsync(id, http.RequestAborted);
        if (found.IsFailure)
            return OpsResults.Fail(found.Error!);

        var account = found.Value;

        var result = await twoFactor.ResetAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username,
                "two_factor.reset", "StaffUser", id.ToString(),
                Reason: $"Two-factor authentication was reset for '{account.Username}'. They must enroll again at next sign-in.", actor.IpAddress),
                http.RequestAborted);

        return OpsResults.Ok(new { staffUserId = id, reset = true });
    }

    /// <summary>The catalog a settings screen renders. Grouped, and labelled for humans — a checkbox list of
    /// raw dotted codes is how an operator guards the wrong thing.</summary>
    private static IResult GetActions() =>
        OpsResults.Ok(new
        {
            actions = GuardedActions.Guardable.Select(a => new { code = a.Code, group = a.Group, label = a.Label }),
            // Shown as permanently on, so the screen explains its absence rather than leaving an operator
            // hunting for a toggle that will never appear.
            alwaysGuarded = new[]
            {
                new
                {
                    code = GuardedActions.TwoFactorPolicy,
                    group = "Security",
                    label = "Change which actions require two-factor",
                    reason = "Always required. If this could be switched off, every other requirement could be too.",
                },
            },
        });

    private static async Task<IResult> GetPolicyAsync(HttpContext http, ITwoFactorPolicyService policy)
    {
        var view = await policy.GetAsync(http.RequestAborted);

        return OpsResults.Ok(new
        {
            guardedActions = view.Current.GuardedActions,
            // "Configuration" means nobody has chosen yet; "Stored" means someone did — indistinguishable
            // from the values alone, and only one of them is a question worth asking.
            source = view.Current.Source.ToString(),
            updatedBy = view.Current.UpdatedBy,
            updatedAt = view.Current.UpdatedAt,
            note = view.Current.Note,
            configuredDefaults = view.ConfiguredDefaults.GuardedActions,
            enrolledStaffCount = view.EnrolledStaffCount,
        });
    }

    private static async Task<IResult> SavePolicyAsync(
        SaveTwoFactorPolicyRequest request, HttpContext http, ITwoFactorPolicyService policy, IAuditLogger audit)
    {
        // An action nothing enforces is refused rather than stored: a checkbox that guards nothing reads as
        // protection that is not there, which is worse than an absent one.
        var unknown = request.GuardedActions
            .Where(a => !GuardedActions.AllCodes.Contains(a, StringComparer.Ordinal))
            .ToList();

        if (unknown.Count > 0)
        {
            return OpsResults.Bad(
                OpsErrorCodes.UnknownGuardedAction,
                $"Unknown guarded action(s): {string.Join(", ", unknown)}.");
        }

        var actor = AuditActor.From(http);

        var before = await policy.GetAsync(http.RequestAborted);
        var result = await policy.SaveAsync(request.GuardedActions, request.Note, actor.Username, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(
            new LogAuditEntryCommand(
                actor.StaffUserId, actor.Username,
                "two_factor.policy_changed", "TwoFactorPolicy", "platform",
                // Before AND after, so the trail answers "what changed" without diffing two rows by hand.
                Reason: $"Guarded actions changed from [{string.Join(", ", before.Current.GuardedActions)}] " +
                        $"to [{string.Join(", ", result.Value.GuardedActions)}]." +
                        (string.IsNullOrWhiteSpace(request.Note) ? "" : $" Note: {request.Note}"),
                actor.IpAddress),
            http.RequestAborted);

        return OpsResults.Ok(new
        {
            guardedActions = result.Value.GuardedActions,
            source = result.Value.Source.ToString(),
            updatedBy = result.Value.UpdatedBy,
            updatedAt = result.Value.UpdatedAt,
            note = result.Value.Note,
        });
    }

    private static async Task<IResult> GetPolicyHistoryAsync(
        HttpContext http, ITwoFactorPolicyService policy, int limit = 50)
    {
        var history = await policy.GetHistoryAsync(limit, http.RequestAborted);

        return OpsResults.Ok(new
        {
            versions = history.Select(v => new
            {
                id = v.Id,
                guardedActions = v.GuardedActions(),
                updatedBy = v.UpdatedBy,
                updatedAt = v.UpdatedAt,
                note = v.Note,
            }),
        });
    }
}
