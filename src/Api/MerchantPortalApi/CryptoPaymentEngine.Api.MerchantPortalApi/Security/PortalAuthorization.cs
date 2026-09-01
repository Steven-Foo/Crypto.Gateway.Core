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
                return Results.Json(
                    new { isSuccess = false, error = $"Missing permission '{permissionCode}'." },
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(context);
        });

    private static bool Grants(IReadOnlyList<string> permissions, string permissionCode) =>
        permissions.Contains(PortalPermissions.PortalWildcard, StringComparer.Ordinal) ||
        permissions.Contains(permissionCode, StringComparer.Ordinal);
}
