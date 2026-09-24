using System.ComponentModel.DataAnnotations;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

public sealed class PortalTwoFactorCodeRequest
{
    [Required, MaxLength(32)] public string Code { get; init; } = null!;
}

/// <summary>
/// Enrollment and recovery codes for a merchant-portal account.
///
/// <para><b>This phase is login only.</b> There is no guarded-action catalog and no policy on this host —
/// a portal user proves themselves once, at sign-in. When per-action guarding is wanted here (payout
/// approval being the obvious first), the Ops host's <c>GuardedActions</c> pattern transplants with a
/// per-tenant rather than platform-wide policy.</para>
///
/// <para>The self-service routes are authenticated but NOT permission-gated: they act on the caller's own
/// account, taken from the validated session, and gating them would leave a brand-new user unable to enroll
/// and therefore unable to do anything at all.</para>
/// </summary>
public static class PortalTwoFactorEndpoints
{
    public static void MapPortalTwoFactorApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/portal/auth/2fa/status", GetStatusAsync);
        app.MapPost("/api/v1/portal/auth/2fa/enroll", BeginEnrollmentAsync);
        app.MapPost("/api/v1/portal/auth/2fa/enroll/confirm", ConfirmEnrollmentAsync);
        app.MapPost("/api/v1/portal/auth/2fa/recovery-codes", RegenerateRecoveryCodesAsync);

        // A merchant admin recovering one of their OWN users' lost devices, without involving platform
        // staff. Tenant-scoped in the service, so another merchant passing a real id gets nothing.
        app.MapPost("/api/v1/portal/accounts/{id:guid}/2fa/reset", ResetAsync)
            .RequirePortalPermission(PortalPermissions.Accounts.Manage);
    }

    private static async Task<IResult> GetStatusAsync(HttpContext http, IMerchantTwoFactorService twoFactor)
    {
        var principal = PortalTenant.Principal(http);
        var status = await twoFactor.GetStatusAsync(
            principal.MerchantId, principal.MerchantUserId, http.RequestAborted);

        return PortalResults.Ok(new
        {
            enrolled = status.Enrolled,
            status = status.Status?.ToString(),
            enrolledAt = status.EnrolledAt,
            recoveryCodesRemaining = status.RecoveryCodesRemaining,
            lockedOut = status.LockedOut,
        });
    }

    /// <summary>Returns the provisioning URI ONCE. Nothing returns it again — losing the setup means
    /// starting over, which is the correct cost for a secret that must not be re-retrievable later.</summary>
    private static async Task<IResult> BeginEnrollmentAsync(
        HttpContext http, IMerchantTwoFactorService twoFactor, IAuditLogger audit)
    {
        var principal = PortalTenant.Principal(http);

        var result = await twoFactor.BeginEnrollmentAsync(
            principal.MerchantId, principal.MerchantUserId, principal.Username, http.RequestAborted);

        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        await LogAsync(audit, http, PortalAuditActions.TwoFactorEnrollmentStarted,
            principal.MerchantUserId.ToString(), "Two-factor enrollment started.");

        return PortalResults.Ok(new
        {
            provisioningUri = result.Value.ProvisioningUri,
            secret = result.Value.SecretBase32, // for manual entry when a camera is unavailable
        });
    }

    private static async Task<IResult> ConfirmEnrollmentAsync(
        PortalTwoFactorCodeRequest request,
        HttpContext http,
        IMerchantTwoFactorService twoFactor,
        IMerchantAuthService auth,
        IAuditLogger audit,
        IOptions<MerchantSessionCookieOptions> cookieOptions)
    {
        var principal = PortalTenant.Principal(http);

        var result = await twoFactor.ConfirmEnrollmentAsync(
            principal.MerchantId, principal.MerchantUserId, request.Code, http.RequestAborted);

        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        // Upgrade the CURRENT session in place, so the user lands in the portal rather than back at a login
        // form the instant their setup succeeded.
        MerchantSessionCookie.TryReadToken(http, cookieOptions.Value.Name, out var token, out _);
        await auth.CompleteEnrollmentForSessionAsync(token, http.RequestAborted);

        await LogAsync(audit, http, PortalAuditActions.TwoFactorEnrolled,
            principal.MerchantUserId.ToString(), "Two-factor enrollment confirmed.");

        return PortalResults.Ok(new
        {
            enrolled = true,
            recoveryCodes = result.Value, // shown once
        });
    }

    /// <summary>
    /// Always costs a fresh code, verified here rather than through any policy: replacing a credential
    /// proves identity, and this host has no guarded-action policy to hang it on anyway.
    /// </summary>
    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        PortalTwoFactorCodeRequest request, HttpContext http, IMerchantTwoFactorService twoFactor, IAuditLogger audit)
    {
        var principal = PortalTenant.Principal(http);

        var verified = await twoFactor.VerifyAsync(principal.MerchantUserId, request.Code, http.RequestAborted);
        if (verified.IsFailure)
            return PortalResults.Fail(verified.Error!);

        var result = await twoFactor.RegenerateRecoveryCodesAsync(
            principal.MerchantId, principal.MerchantUserId, http.RequestAborted);

        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        await LogAsync(audit, http, PortalAuditActions.TwoFactorRecoveryCodesRegenerated,
            principal.MerchantUserId.ToString(), "Recovery codes regenerated; the previous set was invalidated.");

        return PortalResults.Ok(new { recoveryCodes = result.Value });
    }

    private static async Task<IResult> ResetAsync(
        Guid id, HttpContext http, IMerchantTwoFactorService twoFactor, IMerchantAccountService accounts, IAuditLogger audit)
    {
        var principal = PortalTenant.Principal(http);

        // Confirm the target belongs to THIS tenant before reporting anything. The service's list is already
        // tenant-scoped, so a foreign id simply is not in it and comes back "not found" — never a silent
        // success reported against someone else's account.
        var accountsResult = await accounts.ListAsync(principal.MerchantId, http.RequestAborted);
        if (accountsResult.IsFailure)
            return PortalResults.Fail(accountsResult.Error!);

        var account = accountsResult.Value.FirstOrDefault(a => a.MerchantUserId == id);
        if (account is null)
            return PortalResults.NotFound(PortalErrorCodes.NotFound, "Portal account not found.");

        var result = await twoFactor.ResetAsync(principal.MerchantId, id, http.RequestAborted);
        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        await LogAsync(audit, http, PortalAuditActions.TwoFactorReset, id.ToString(),
            $"Two-factor authentication was reset for '{account.Username}'. They must enroll again at next sign-in.");

        return PortalResults.Ok(new { merchantUserId = id, reset = true });
    }

    /// <summary>Every entry is stamped with the tenant by PortalAuditActor, so it shows in that merchant's
    /// own activity log and in no other tenant's.</summary>
    private static Task LogAsync(IAuditLogger audit, HttpContext http, string action, string entityId, string reason) =>
        audit.LogAsync(
            PortalAuditActor.From(http).Entry(action, PortalAuditActions.EntityAccount, entityId, reason),
            http.RequestAborted);
}
