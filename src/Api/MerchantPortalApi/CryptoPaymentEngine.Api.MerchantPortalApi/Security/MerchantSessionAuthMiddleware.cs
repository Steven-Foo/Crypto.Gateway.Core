using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// The authentication + <b>tenant</b> boundary for every merchant-portal request. Same model as the Ops host's
/// <c>StaffBearerAuthMiddleware</c> — an opaque session token accepted from an httpOnly cookie (browser) or an
/// <c>Authorization: Bearer</c> header (non-browser), with CSRF enforced on cookie-authenticated writes — but
/// the validated <see cref="MerchantPrincipal"/> carries the session's <c>MerchantId</c>. Endpoints read the
/// tenant scope from that principal and <b>never</b> from the request (§ tenant-isolation): a merchant cannot
/// address another merchant's data because it never names a merchant at all.
///
/// Login, health, and swagger are the only unauthenticated paths; CORS preflight (OPTIONS) bypasses auth.
/// </summary>
public sealed class MerchantSessionAuthMiddleware(RequestDelegate next)
{
    public const string PrincipalItem = "MerchantPrincipal";
    public const string CsrfHeader = "X-CSRF-Token";
    private const string LoginPath = "/api/v1/portal/auth/login";

    public async Task InvokeAsync(
        HttpContext context, IMerchantSessionValidator validator, IOptions<MerchantSessionCookieOptions> cookieOptions)
    {
        var path = context.Request.Path.Value ?? "";
        if (HttpMethods.IsOptions(context.Request.Method) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!MerchantSessionCookie.TryReadToken(context, cookieOptions.Value.Name, out var token, out var fromCookie))
        {
            await Fail(context, Endpoints.PortalErrorCodes.Unauthenticated, "Missing session. Sign in for a session cookie, or provide an 'Authorization: Bearer <token>' header.");
            return;
        }

        var result = await validator.ValidateAsync(token, context.RequestAborted);
        if (result.IsFailure)
        {
            await Fail(context, result.Error!.Code, result.Error!.Message);
            return;
        }

        if (fromCookie && IsUnsafeMethod(context.Request.Method))
        {
            var presented = context.Request.Headers[CsrfHeader].ToString();
            if (!CsrfMatches(presented, result.Value.CsrfToken))
            {
                await Forbid(context, Endpoints.PortalErrorCodes.CsrfInvalid, $"Missing or invalid CSRF token. Send the session's csrfToken as the '{CsrfHeader}' header.");
                return;
            }
        }

        context.Items[PrincipalItem] = result.Value;
        await next(context);
    }

    private static bool IsUnsafeMethod(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    private static bool CsrfMatches(string presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // Auth failures carry an errorCode like every other response on this host: a SPA must be able to tell
    // "your session expired, sign in again" from "your CSRF token is stale, re-read it from /auth/me" without
    // pattern-matching prose. Both are otherwise indistinguishable 401/403s.
    private static Task Fail(HttpContext context, string errorCode, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { isSuccess = false, data = (object?)null, error = message, errorCode });
    }

    private static Task Forbid(HttpContext context, string errorCode, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { isSuccess = false, data = (object?)null, error = message, errorCode });
    }
}
