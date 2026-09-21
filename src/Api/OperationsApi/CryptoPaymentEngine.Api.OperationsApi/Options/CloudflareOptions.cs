namespace CryptoPaymentEngine.Api.OperationsApi.Options;

/// <summary>Disabled by default — real deployments supply a real ApiToken/ZoneId; until then, IP
/// allowlist changes still persist to the DB, they just don't reach Cloudflare (matches APIGateway's
/// <c>CloudflareOptions</c> exactly).</summary>
public sealed class CloudflareOptions
{
    public bool Enabled { get; set; }
    public string ApiToken { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>The exact merchant-API hostname (e.g. <c>stagapi2.udigipay.com</c>) the Custom Rule blocks
    /// non-allowlisted IPs from reaching. Empty ⇒ <see cref="CloudflareService.SyncCustomRuleAllowlistAsync"/>
    /// is skipped entirely — the legacy IP Access Rules sync (<see cref="CloudflareService.AddIpAsync"/>/
    /// <see cref="CloudflareService.RemoveIpAsync"/>) does not block anything by itself and needs no hostname.</summary>
    public string CustomRuleHostname { get; set; } = string.Empty;

    /// <summary>Account-level id (Cloudflare dashboard sidebar — "Account ID", NOT the same as ZoneId). Required
    /// only for the Lists-based sync; the Lists API lives under <c>/accounts/{account_id}/...</c>, a different
    /// scope from every other call this service makes.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Name of the Cloudflare account-level IP List that holds every merchant's allowed IPs. Cloudflare's
    /// own naming rule: lowercase letters, digits and underscore only, max 50 characters (e.g.
    /// <c>merchant_ip_allowlist</c>). Set alongside <see cref="AccountId"/> to switch
    /// <see cref="CloudflareService.SyncCustomRuleAllowlistAsync"/> onto the Lists path — no per-expression size
    /// ceiling, unlike the inline fallback. Leave either empty to keep the inline path.</summary>
    public string IpListName { get; set; } = string.Empty;
}
