using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// Route-level permission gate on top of <see cref="MerchantSessionAuthMiddleware"/> — the real authorization
/// boundary for the portal, alongside the tenant scope. The permission set is exactly what was snapshotted onto
/// the caller's session at login; a role holding the wildcard passes every check, and an account with no role
/// holds nothing and therefore passes none (fail-closed).
///
/// <para>Server-enforced, not merely used to drive what the SPA renders — a hidden button is a UX nicety, this
/// filter is the boundary (§10). Note it is orthogonal to tenant isolation: this decides <em>what</em> a caller
/// may do, while the session's <c>MerchantId</c> decides <em>whose data</em> it may do it to.</para>
/// </summary>
public static class PortalAuthorization
{
    public static RouteHandlerBuilder RequirePortalPermission(this RouteHandlerBuilder builder, string permissionCode) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var principal = context.HttpContext.Items[MerchantSessionAuthMiddleware.PrincipalItem] as
                CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application.MerchantPrincipal;

            if (principal is null || !Grants(principal.Permissions, permissionCode))
                // A permission-denied 403 and a CSRF-failure 403 are otherwise indistinguishable to a SPA —
                // one means "ask your admin for access", the other "re-read your CSRF token and retry".
                return Endpoints.PortalResults.Forbidden(
                    Endpoints.PortalErrorCodes.PermissionDenied, $"Missing permission '{permissionCode}'.");

            return await next(context);
        });

    private static bool Grants(IReadOnlyList<string> permissions, string permissionCode) =>
        permissions.Contains(PortalPermissions.PortalWildcard, StringComparer.Ordinal) ||
        permissions.Contains(permissionCode, StringComparer.Ordinal);

    /// <summary>
    /// Demands a fresh authenticator code for a sensitive portal action — IF this merchant's effective policy
    /// guards it. The effective policy is the platform minimum (set by platform staff, cannot be removed by the
    /// merchant) ∪ the merchant's own additions (set in the portal) ∪ the always-on
    /// <see cref="MerchantGuardedActions.TwoFactorPolicy"/>. Chain it AFTER <see cref="RequirePortalPermission"/>:
    /// no point asking for a code from someone who may not do the thing at all.
    ///
    /// <para>Same contract as the admin back office's <c>RequireTwoFactor</c>, so one frontend interceptor
    /// pattern serves both: no <c>X-2FA-Code</c> ⇒ 403 <c>portal.two_factor_required</c> naming the action in
    /// <c>data.action</c>; wrong code ⇒ the 2FA error; right code ⇒ the handler runs. A refused code means the
    /// action did not happen — the check runs before the handler.</para>
    /// </summary>
    public static RouteHandlerBuilder RequirePortalTwoFactor(this RouteHandlerBuilder builder, string action) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Items[MerchantSessionAuthMiddleware.PrincipalItem] is not MerchantPrincipal principal)
                return Endpoints.PortalResults.Unauthorized(Endpoints.PortalErrorCodes.Unauthenticated, "Missing session.");

            var policy = await http.RequestServices
                .GetRequiredService<MerchantTwoFactorPolicyProvider>()
                .GetForMerchantAsync(principal.MerchantId, http.RequestAborted);

            if (!policy.IsGuarded(action))
                return await next(context);

            // The account's own switch (set by an admin — never by the user themselves) overrides the list:
            // off means this account does everything without 2FA, exactly as for staff.
            if (!principal.RequireTwoFactor)
                return await next(context);

            // Fail closed. Unreachable while enrollment is forced (the middleware confines an unenrolled
            // session to the enrollment routes), kept because that is an invariant elsewhere.
            if (!principal.TwoFactorEnrolled)
                return Endpoints.PortalResults.Forbidden(
                    Endpoints.PortalErrorCodes.TwoFactorNotEnrolled,
                    "This account has not set up two-factor authentication.");

            // A recovery code gets someone back in; it does not authorise moving money.
            if (!principal.AuthenticatorProven)
                return Endpoints.PortalResults.Forbidden(
                    Endpoints.PortalErrorCodes.TwoFactorRecoveryNotAccepted,
                    "Sign in with your authenticator app to perform this action.");

            var code = http.Request.Headers[TwoFactorCodeHeader].ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.Json(
                    new
                    {
                        isSuccess = false,
                        error = "This action requires a code from your authenticator app.",
                        errorCode = Endpoints.PortalErrorCodes.TwoFactorRequired,
                        data = new { action },
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var verified = await http.RequestServices
                .GetRequiredService<IMerchantTwoFactorService>()
                .VerifyAsync(principal.MerchantUserId, code, http.RequestAborted);

            return verified.IsFailure ? Endpoints.PortalResults.Fail(verified.Error!) : await next(context);
        });

    /// <summary>The per-request code header — the same name the admin back office uses. <b>Never log it.</b></summary>
    public const string TwoFactorCodeHeader = "X-2FA-Code";
}
