using System.Security.Cryptography;
using System.Text;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// The authentication boundary for every Ops request. A session is a server-side opaque token
/// (<see cref="IStaffSessionValidator"/>); this middleware accepts it from EITHER an
/// <c>Authorization: Bearer</c> header (non-browser clients + the UI's interim bearer mode) OR an httpOnly
/// session cookie (the UI's target cookie mode, §12). The trust model is identical — only delivery differs.
///
/// <para><b>CSRF:</b> a cookie is <em>ambient</em> (the browser attaches it automatically), so an unsafe
/// (state-changing) cookie-authenticated request must also echo the session's CSRF token in the
/// <c>X-CSRF-Token</c> header — a synchronizer token bound to the session (§ StaffSession.CsrfToken). A
/// bearer-header request is not ambient (script must set the header), so it is inherently CSRF-safe and exempt.
/// Safe methods (GET/HEAD/OPTIONS) never require a CSRF token.</para>
///
/// Login, health, swagger, and CORS preflight (OPTIONS) are the only unauthenticated paths.
/// </summary>
public sealed class StaffBearerAuthMiddleware(RequestDelegate next)
{
    public const string PrincipalItem = "StaffPrincipal";
    public const string CsrfHeader = "X-CSRF-Token";
    private const string LoginPath = "/api/v1/ops/auth/login";

    public async Task InvokeAsync(HttpContext context, IStaffSessionValidator validator, IOptions<OpsSessionCookieOptions> cookieOptions)
    {
        var path = context.Request.Path.Value ?? "";
        if (HttpMethods.IsOptions(context.Request.Method) || // CORS preflight is unauthenticated by design
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!OpsSessionCookie.TryReadToken(context, cookieOptions.Value.Name, out var token, out var fromCookie))
        {
            await Fail(context, "Missing session. Provide an 'Authorization: Bearer <token>' header or sign in for a session cookie.");
            return;
        }

        var result = await validator.ValidateAsync(token, context.RequestAborted);
        if (result.IsFailure)
        {
            await Fail(context, result.Error!.Message);
            return;
        }

        // A cookie-authenticated state-changing request must carry the session's CSRF token.
        if (fromCookie && IsUnsafeMethod(context.Request.Method))
        {
            var presented = context.Request.Headers[CsrfHeader].ToString();
            if (!CsrfMatches(presented, result.Value.CsrfToken))
            {
                await Forbid(context, $"Missing or invalid CSRF token. Send the session's csrfToken as the '{CsrfHeader}' header.");
                return;
            }
        }

        context.Items[PrincipalItem] = result.Value;
        await next(context);
    }

    private static bool IsUnsafeMethod(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    /// <summary>Constant-time comparison so a token value can't be recovered by timing. Different lengths are
    /// rejected (leaks only length, which is fixed here anyway).</summary>
    private static bool CsrfMatches(string presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
            return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static Task Fail(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { isSuccess = false, error = message });
    }

    private static Task Forbid(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new { isSuccess = false, error = message });
    }
}
