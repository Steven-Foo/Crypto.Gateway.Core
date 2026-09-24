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
        var result = await auth.LoginAsync(
            new LoginCommand(request.Username, request.Password, request.Code), http.RequestAborted);

        if (result.IsFailure)
        {
            // A two-factor failure keeps its OWN code (two_factor.code_required, two_factor.invalid_code,
            // two_factor.locked_out) rather than collapsing into ops.invalid_credentials. The client has to
            // tell "your password was wrong, start again" from "your code was wrong, the password was fine"
            // — the second re-prompts for a code, and flattening them would force a full re-login on every
            // mistyped digit. It leaks nothing new: the caller already supplied a correct password to reach
            // this point.
            var error = result.Error!;
            return error.Code.StartsWith("two_factor.", StringComparison.Ordinal)
                ? OpsResults.Fail(error)
                : OpsResults.Unauthorized(OpsErrorCodes.InvalidCredentials, error.Message);
        }

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
                // False => this session may reach ONLY the enrollment routes. Returned so the SPA can route
                // straight to the QR screen, rather than discovering the restriction one failed call at a time.
                twoFactorEnrolled = result.Value.TwoFactorEnrolled,
                // "RecoveryCode" signs in but cannot authorise a guarded action — worth nudging the user to
                // re-enroll now rather than letting them find out at the worst possible moment.
                twoFactorMethod = result.Value.TwoFactorMethod?.ToString(),
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http, IStaffAuthService auth, IOptions<OpsSessionCookieOptions> cookieOptions, IHostEnvironment env)
    {
        // Revoke whichever session the caller presented — bearer header or cookie — then clear the cookie.
        OpsSessionCookie.TryReadToken(http, cookieOptions.Value.Name, out var token, out _);

        await auth.LogoutAsync(token, http.RequestAborted);
        OpsSessionCookie.Delete(http, cookieOptions.Value, env.IsDevelopment());
        return Results.Ok(new { isSuccess = true, data = new { loggedOut = true }, error = (string?)null, errorCode = (string?)null });
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
                // Survives a page refresh, which the login response does not — this is where the SPA learns
                // on load that it must finish enrollment before anything else will answer.
                twoFactorEnrolled = principal.TwoFactorEnrolled,
                authenticatorProven = principal.AuthenticatorProven,
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }
}
