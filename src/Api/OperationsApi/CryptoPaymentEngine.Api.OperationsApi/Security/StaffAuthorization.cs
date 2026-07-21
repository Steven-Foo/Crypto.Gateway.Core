using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Domain;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>Route-level role gate on top of <see cref="StaffBearerAuthMiddleware"/> — Admin-only mutations.</summary>
public static class StaffAuthorization
{
    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var principal = context.HttpContext.Items[StaffBearerAuthMiddleware.PrincipalItem] as StaffPrincipal;
            if (principal is null || principal.Role != StaffRole.Admin)
                return Results.Json(new { isSuccess = false, error = "Admin role required." }, statusCode: StatusCodes.Status403Forbidden);

            return await next(context);
        });
}
