namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>
/// Config for the httpOnly session cookie (bound from <c>Auth:Cookie</c>). The cookie carries the SAME opaque
/// session token the bearer header does — auth trust is unchanged, only delivery (§ StaffBearerAuthMiddleware).
/// Defaults are secure; a deployment tunes <see cref="SameSite"/>/<see cref="Secure"/> for its UI↔API topology.
/// </summary>
public sealed class OpsSessionCookieOptions
{
    public const string SectionName = "Auth:Cookie";

    /// <summary>The httpOnly cookie name carrying the session token.</summary>
    public string Name { get; init; } = "cpe_ops_session";

    /// <summary>
    /// <c>Lax</c> (default) is correct for dev (localhost:port→localhost:port is same-site) and for a UI + API
    /// on the same registrable domain (e.g. admin.example.com + api.example.com). Use <c>None</c> only when the
    /// UI and API are on different registrable domains (a true cross-site split) — it requires <see cref="Secure"/>
    /// and leans entirely on the CSRF token + CORS allow-list. <c>Strict</c> is the tightest but drops the cookie
    /// on cross-site top-level navigations too.
    /// </summary>
    public string SameSite { get; init; } = "Lax";

    /// <summary>HTTPS-only cookie. Null ⇒ default by environment: false in Development (so http://localhost
    /// works), true otherwise. A <c>SameSite=None</c> cookie is always forced Secure regardless (browsers drop
    /// a non-Secure None cookie).</summary>
    public bool? Secure { get; init; }
}
