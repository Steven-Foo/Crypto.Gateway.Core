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
                return Results.Json(
                    new { isSuccess = false, error = $"Missing permission '{permissionCode}'." },
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(context);
        });

    private static bool Grants(StaffPrincipal principal, string permissionCode) =>
        principal.Permissions.Contains(Role.WildcardPermission, StringComparer.Ordinal) ||
        principal.Permissions.Contains(permissionCode, StringComparer.Ordinal);
}
