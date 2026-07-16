namespace CryptoPaymentEngine.Api.OperationsApi.Options;

/// <summary>Disabled by default — real deployments supply a real ApiToken/ZoneId; until then, IP
/// allowlist changes still persist to the DB, they just don't reach Cloudflare (matches APIGateway's
/// <c>CloudflareOptions</c> exactly).</summary>
public sealed class CloudflareOptions
{
    public bool Enabled { get; set; }
    public string ApiToken { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
}
