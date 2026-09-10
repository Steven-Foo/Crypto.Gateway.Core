using CryptoPaymentEngine.Api.MerchantPortalApi.Models;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// Merchant-portal auth: login/logout/me. Same httpOnly-cookie + CSRF model as the Ops host — login sets the
/// cookie AND returns <c>token</c> (bearer mode) + <c>csrfToken</c>. Every session is bound to one tenant
/// (<c>merchantId</c>), which is what scopes every other portal endpoint.
/// </summary>
public static class PortalAuthEndpoints
{
    public static void MapPortalAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/portal/auth/login", LoginAsync); // the one unauthenticated portal endpoint
        app.MapPost("/api/v1/portal/auth/logout", LogoutAsync);
        app.MapGet("/api/v1/portal/auth/me", GetMe);
    }

    private static async Task<IResult> LoginAsync(
        PortalLoginRequest request,
        IMerchantAuthService auth,
        IOptions<MerchantSessionCookieOptions> cookieOptions,
        IHostEnvironment env,
        HttpContext http)
    {
        var result = await auth.LoginAsync(new MerchantLoginCommand(request.Username, request.Password), http.RequestAborted);
        if (result.IsFailure)
            // The module's own code (e.g. merchantidentity.invalid_credentials) reaches the client, so a SPA can
            // distinguish bad credentials from a disabled account without reading the message.
            return PortalResults.Unauthorized(result.Error!.Code, result.Error!.Message);

        MerchantSessionCookie.Append(http, cookieOptions.Value, env.IsDevelopment(), result.Value.Token, result.Value.ExpiresAt);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                token = result.Value.Token,
                csrfToken = result.Value.CsrfToken,
                expiresAt = result.Value.ExpiresAt,
                merchantId = result.Value.MerchantId,
                username = result.Value.Username,
                displayName = result.Value.DisplayName,
                permissions = result.Value.Permissions,
                // UX signal only (not a security control): the SPA shows a change-password step at first login.
                mustChangePassword = result.Value.MustChangePassword,
            },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http, IMerchantAuthService auth, IOptions<MerchantSessionCookieOptions> cookieOptions, IHostEnvironment env)
    {
        MerchantSessionCookie.TryReadToken(http, cookieOptions.Value.Name, out var token, out _);

        await auth.LogoutAsync(token, http.RequestAborted);
        MerchantSessionCookie.Delete(http, cookieOptions.Value, env.IsDevelopment());
        return Results.Ok(new { isSuccess = true, data = new { loggedOut = true }, error = (string?)null, errorCode = (string?)null });
    }

    /// <summary>What the portal renders nav/identity from. Reads straight off the validated session; also
    /// returns <c>csrfToken</c> so the SPA can re-obtain it after a refresh (the session cookie is httpOnly).</summary>
    private static IResult GetMe(HttpContext http)
    {
        var principal = PortalTenant.Principal(http);
        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = principal.MerchantId,
                merchantUserId = principal.MerchantUserId,
                username = principal.Username,
                displayName = principal.DisplayName,
                permissions = principal.Permissions,
                csrfToken = principal.CsrfToken,
            },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }
}
