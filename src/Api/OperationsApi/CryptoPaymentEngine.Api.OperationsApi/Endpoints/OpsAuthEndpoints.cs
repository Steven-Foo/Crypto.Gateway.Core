using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

public static class OpsAuthEndpoints
{
    public static void MapOpsAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/auth/login", LoginAsync); // the one unauthenticated Ops endpoint
        app.MapPost("/api/v1/ops/auth/logout", LogoutAsync);
        app.MapGet("/api/v1/ops/auth/me", GetMe); // any valid session — no specific permission required
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IStaffAuthService auth,
        IOptions<OpsSessionCookieOptions> cookieOptions,
        IHostEnvironment env,
        HttpContext http)
    {
        var result = await auth.LoginAsync(new LoginCommand(request.Username, request.Password), http.RequestAborted);
        if (result.IsFailure)
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: StatusCodes.Status401Unauthorized);

        // Set the httpOnly session cookie (the UI's cookie mode reads nothing from the body but this). We ALSO
        // return `token` so the UI's interim bearer mode keeps working from one login endpoint (§12) — a client
        // uses one or the other. `csrfToken` is for cookie mode: echo it as the X-CSRF-Token header on writes.
        OpsSessionCookie.Append(http, cookieOptions.Value, env.IsDevelopment(), result.Value.Token, result.Value.ExpiresAt);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                token = result.Value.Token,
                csrfToken = result.Value.CsrfToken,
                expiresAt = result.Value.ExpiresAt,
                username = result.Value.Username,
                role = result.Value.RoleName,
                permissions = result.Value.Permissions,
            },
            error = (string?)null,
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http, IStaffAuthService auth, IOptions<OpsSessionCookieOptions> cookieOptions, IHostEnvironment env)
    {
        // Revoke whichever session the caller presented — bearer header or cookie — then clear the cookie.
        OpsSessionCookie.TryReadToken(http, cookieOptions.Value.Name, out var token, out _);

        await auth.LogoutAsync(token, http.RequestAborted);
        OpsSessionCookie.Delete(http, cookieOptions.Value, env.IsDevelopment());
        return Results.Ok(new { isSuccess = true, data = new { loggedOut = true }, error = (string?)null });
    }

    /// <summary>
    /// What the frontend renders module/button visibility from — reads straight off the already-validated
    /// session in <c>HttpContext.Items</c> (§ StaffBearerAuthMiddleware), no extra DB round trip. The same
    /// codes here are independently re-checked server-side by <c>RequirePermission</c> on every mutating
    /// endpoint (§10) — this is a convenience projection, not the authorization boundary itself. Also returns
    /// <c>csrfToken</c>: the SPA calls this on load to re-obtain the (in-memory-only) CSRF token after a refresh
    /// dropped it, since the session cookie is httpOnly and JS cannot read it back.
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
                csrfToken = principal.CsrfToken,
            },
            error = (string?)null,
        });
    }
}
