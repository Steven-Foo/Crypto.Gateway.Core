using CryptoPaymentEngine.Api.OperationsApi.Endpoints;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// Route-level permission gate on top of <see cref="StaffBearerAuthMiddleware"/> — the real authorization
/// boundary. <see cref="StaffPrincipal.Permissions"/> is the exact set snapshotted onto the caller's session
/// at login (§ StaffSession); a role holding <see cref="Role.WildcardPermission"/> passes every check. This
/// is deliberately server-enforced, not just used to drive what the frontend renders — a frontend hiding a
/// button is a UX nicety, this filter is the actual boundary (§10).
/// </summary>
public static class StaffAuthorization
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permissionCode) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var principal = context.HttpContext.Items[StaffBearerAuthMiddleware.PrincipalItem] as StaffPrincipal;
            if (principal is null || !Grants(principal, permissionCode))
                return OpsResults.Forbidden(
                    OpsErrorCodes.PermissionDenied, $"Missing permission '{permissionCode}'.");

            return await next(context);
        });

    private static bool Grants(StaffPrincipal principal, string permissionCode) =>
        principal.Permissions.Contains(Role.WildcardPermission, StringComparer.Ordinal) ||
        principal.Permissions.Contains(permissionCode, StringComparer.Ordinal);

    /// <summary>
    /// Demands a fresh authenticator code on this route, when the platform-wide policy says
    /// <paramref name="action"/> is guarded.
    ///
    /// <para><b>Order matters: chain this AFTER <see cref="RequirePermission"/>.</b> There is no point
    /// demanding a code for something the caller may not do anyway, and doing so would confirm the action
    /// exists to someone with no access to it. Filters run in the order they are added.</para>
    ///
    /// <para>The code rides in the <c>X-2FA-Code</c> header rather than the body, so no request model
    /// changes and one frontend interceptor covers every guarded action ever added: catch
    /// <c>ops.two_factor_required</c>, prompt, replay the original request with the header.</para>
    ///
    /// <para><b>It runs before the handler</b>, so a refused code means the action never happened — there is
    /// no partial write to undo. A 401 that still performed the action is the worst outcome available here,
    /// and it is what this ordering prevents.</para>
    /// </summary>
    public static RouteHandlerBuilder RequireTwoFactor(this RouteHandlerBuilder builder, string action) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var principal = http.Items[StaffBearerAuthMiddleware.PrincipalItem] as StaffPrincipal;
            if (principal is null)
                return OpsResults.Unauthorized(OpsErrorCodes.Unauthenticated, "Missing session.");

            var policy = await http.RequestServices
                .GetRequiredService<TwoFactorPolicyProvider>()
                .GetAsync(http.RequestAborted);

            if (!policy.IsGuarded(action))
                return await next(context);

            // Fail closed. An unenrolled operator is refused, never waved through — the dangerous version of
            // this check passes an account that has no factor at all. Unreachable while enrollment is forced
            // (§ StaffBearerAuthMiddleware), and kept precisely because that is an invariant elsewhere.
            if (!principal.TwoFactorEnrolled)
            {
                return OpsResults.Forbidden(
                    OpsErrorCodes.TwoFactorNotEnrolled,
                    "This account has not set up two-factor authentication.");
            }

            // A recovery code signs you in; it does not authorise moving money. Otherwise the control
            // degrades to "whoever holds the printout".
            if (!principal.AuthenticatorProven)
            {
                return OpsResults.Forbidden(
                    OpsErrorCodes.TwoFactorRecoveryNotAccepted,
                    "Sign in with your authenticator app to perform this action.");
            }

            var code = http.Request.Headers[TwoFactorCodeHeader].ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.Json(
                    new
                    {
                        isSuccess = false,
                        error = "This action requires a code from your authenticator app.",
                        errorCode = OpsErrorCodes.TwoFactorRequired,
                        // The action is echoed so the prompt can name what it is protecting, rather than
                        // asking for a code with no explanation of why.
                        data = new { action },
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var verified = await http.RequestServices
                .GetRequiredService<ITwoFactorService>()
                .VerifyAsync(principal.StaffUserId, code, http.RequestAborted);

            return verified.IsFailure ? OpsResults.Fail(verified.Error!) : await next(context);
        });

    /// <summary>The per-request code header. <b>Never log this</b> — with no replay guard a code stays usable
    /// for its own window, so one sitting in a request log is usable by whoever reads that log
    /// (docs/two-factor-authentication.md §6.1).</summary>
    public const string TwoFactorCodeHeader = "X-2FA-Code";
}
