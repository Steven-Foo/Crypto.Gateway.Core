using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

public static class OpsAuthEndpoints
{
    public static void MapOpsAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/auth/login", LoginAsync); // the one unauthenticated Ops endpoint
        app.MapPost("/api/v1/ops/auth/logout", LogoutAsync);
        app.MapGet("/api/v1/ops/auth/me", GetMe); // any valid session — no specific permission required
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, IStaffAuthService auth, HttpContext http)
    {
        var result = await auth.LoginAsync(new LoginCommand(request.Username, request.Password), http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status401Unauthorized);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                token = result.Value.Token,
                expiresAt = result.Value.ExpiresAt,
                username = result.Value.Username,
                role = result.Value.RoleName,
                permissions = result.Value.Permissions,
            },
            error = (string?)null,
        });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, IStaffAuthService auth)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : header;

        await auth.LogoutAsync(token, http.RequestAborted);
        return Results.Ok(new { isSuccess = true, data = new { loggedOut = true }, error = (string?)null });
    }

    /// <summary>
    /// What the frontend renders module/button visibility from — reads straight off the already-validated
    /// session in <c>HttpContext.Items</c> (§ StaffBearerAuthMiddleware), no extra DB round trip. The same
    /// codes here are independently re-checked server-side by <c>RequirePermission</c> on every mutating
    /// endpoint (§10) — this is a convenience projection, not the authorization boundary itself.
    /// </summary>
    private static IResult GetMe(HttpContext http)
    {
        var principal = (StaffPrincipal)http.Items[StaffBearerAuthMiddleware.PrincipalItem]!;
        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                staffUserId = principal.StaffUserId,
                username = principal.Username,
                role = principal.RoleName,
                permissions = principal.Permissions,
            },
            error = (string?)null,
        });
    }
}
