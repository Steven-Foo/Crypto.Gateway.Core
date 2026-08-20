namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// Reads/writes the httpOnly session cookie and resolves the session token from a request. The cookie carries
/// the same opaque token the bearer header does — this is delivery, not a new trust model (§10). Deleting uses
/// the same attributes as writing, or the browser won't match and remove it.
/// </summary>
public static class OpsSessionCookie
{
    public static void Append(
        HttpContext http, OpsSessionCookieOptions options, bool isDevelopment, string token, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(options.Name, token, Build(options, isDevelopment, expiresAt));

    public static void Delete(HttpContext http, OpsSessionCookieOptions options, bool isDevelopment) =>
        http.Response.Cookies.Delete(options.Name, Build(options, isDevelopment, expiresAt: null));

    /// <summary>
    /// Resolves the session token from a request, preferring the <c>Authorization: Bearer</c> header (used by
    /// non-browser clients and the UI's interim bearer mode — inherently CSRF-safe as it is not ambient) over
    /// the session cookie. <paramref name="fromCookie"/> tells the caller whether CSRF enforcement applies.
    /// </summary>
    public static bool TryReadToken(HttpContext http, string cookieName, out string token, out bool fromCookie)
    {
        token = string.Empty;
        fromCookie = false;

        if (http.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var value = header.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = value["Bearer ".Length..].Trim();
                if (!string.IsNullOrEmpty(token))
                    return true;
            }
        }

        if (http.Request.Cookies.TryGetValue(cookieName, out var cookie) && !string.IsNullOrEmpty(cookie))
        {
            token = cookie;
            fromCookie = true;
            return true;
        }

        return false;
    }

    private static CookieOptions Build(OpsSessionCookieOptions options, bool isDevelopment, DateTimeOffset? expiresAt)
    {
        var sameSite = ParseSameSite(options.SameSite);

        // Secure defaults by environment (false in dev so http://localhost works). SameSite=None is only honoured
        // when Secure, so force it — a non-Secure None cookie is silently dropped by browsers.
        var secure = options.Secure ?? !isDevelopment;
        if (sameSite == SameSiteMode.None)
            secure = true;

        var cookie = new CookieOptions
        {
            HttpOnly = true,       // never readable by JS — the session token must not reach script (§10, §12)
            Secure = secure,
            SameSite = sameSite,
            Path = "/",
            IsEssential = true,    // an auth cookie is exempt from consent gating
        };

        if (expiresAt is { } expiry)
            cookie.Expires = expiry; // align browser expiry with the server-side session TTL

        return cookie;
    }

    private static SameSiteMode ParseSameSite(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax,
    };
}
