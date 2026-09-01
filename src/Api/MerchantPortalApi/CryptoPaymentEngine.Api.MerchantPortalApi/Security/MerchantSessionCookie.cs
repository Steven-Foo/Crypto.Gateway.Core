namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>Reads/writes the httpOnly merchant-portal session cookie and resolves the session token from a
/// request. Mirrors the Ops host's <c>OpsSessionCookie</c>.</summary>
public static class MerchantSessionCookie
{
    public static void Append(
        HttpContext http, MerchantSessionCookieOptions options, bool isDevelopment, string token, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(options.Name, token, Build(options, isDevelopment, expiresAt));

    public static void Delete(HttpContext http, MerchantSessionCookieOptions options, bool isDevelopment) =>
        http.Response.Cookies.Delete(options.Name, Build(options, isDevelopment, expiresAt: null));

    /// <summary>Resolves the session token, preferring the <c>Authorization: Bearer</c> header (non-browser
    /// clients — inherently CSRF-safe) over the cookie. <paramref name="fromCookie"/> tells the caller whether
    /// CSRF enforcement applies.</summary>
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

    private static CookieOptions Build(MerchantSessionCookieOptions options, bool isDevelopment, DateTimeOffset? expiresAt)
    {
        var sameSite = ParseSameSite(options.SameSite);

        var secure = options.Secure ?? !isDevelopment;
        if (sameSite == SameSiteMode.None)
            secure = true;

        var cookie = new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = sameSite,
            Path = "/",
            IsEssential = true,
        };

        if (expiresAt is { } expiry)
            cookie.Expires = expiry;

        return cookie;
    }

    private static SameSiteMode ParseSameSite(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "strict" => SameSiteMode.Strict,
        "none" => SameSiteMode.None,
        _ => SameSiteMode.Lax,
    };
}
