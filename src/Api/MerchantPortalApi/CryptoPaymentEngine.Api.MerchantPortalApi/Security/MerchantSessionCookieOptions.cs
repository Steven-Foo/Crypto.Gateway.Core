namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// Config for the merchant-portal httpOnly session cookie (bound from <c>Auth:Cookie</c>). Same model as the
/// Ops host's <c>OpsSessionCookieOptions</c> — the cookie carries the opaque session token; auth trust is
/// unchanged, only delivery. Defaults are secure; a deployment tunes SameSite/Secure for its UI↔API topology.
/// </summary>
public sealed class MerchantSessionCookieOptions
{
    public const string SectionName = "Auth:Cookie";

    public string Name { get; init; } = "cpe_portal_session";

    /// <summary><c>Lax</c> (default) suits dev + a same-registrable-domain UI/API; <c>None</c> (forces Secure)
    /// only for a cross-site split; <c>Strict</c> is tightest.</summary>
    public string SameSite { get; init; } = "Lax";

    /// <summary>HTTPS-only. Null ⇒ default by environment: false in Development (so http://localhost works),
    /// true otherwise. A <c>SameSite=None</c> cookie is always forced Secure.</summary>
    public bool? Secure { get; init; }
}
