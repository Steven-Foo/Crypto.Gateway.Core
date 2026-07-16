using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CryptoPaymentEngine.Api.OperationsApi.Options;
using Microsoft.Extensions.Options;

namespace CryptoPaymentEngine.Api.OperationsApi.Services;

/// <summary>
/// Pushes merchant IP-allowlist changes to Cloudflare firewall access rules — ported directly from
/// APIGateway's <c>Bo/Services/CloudflareService.cs</c>. A no-op when <see cref="CloudflareOptions.Enabled"/>
/// is false (no real Cloudflare account wired up yet); a failed call is logged, never thrown, so a
/// Cloudflare outage can never block a merchant IP change from persisting to the DB.
/// </summary>
public sealed class CloudflareService(HttpClient http, IOptions<CloudflareOptions> opts, ILogger<CloudflareService> logger)
{
    private readonly CloudflareOptions _opts = opts.Value;

    public async Task AddIpAsync(string ip, string notes = "", CancellationToken ct = default)
    {
        if (!_opts.Enabled) return;

        try
        {
            var existing = await FindRuleIdAsync(ip, ct);
            if (existing is not null)
            {
                logger.LogDebug("IP {Ip} already whitelisted in Cloudflare (rule {RuleId})", ip, existing);
                return;
            }

            var response = await http.PostAsJsonAsync(
                $"zones/{_opts.ZoneId}/firewall/access_rules/rules",
                new { mode = "whitelist", configuration = new { target = "ip", value = ip }, notes },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Cloudflare AddIp failed for {Ip}: {Status} — {Body}", ip, (int)response.StatusCode, body);
            }
            else
            {
                logger.LogInformation("Cloudflare: whitelisted {Ip}", ip);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloudflare AddIp threw for {Ip}", ip);
        }
    }

    public async Task RemoveIpAsync(string ip, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return;

        try
        {
            var ruleId = await FindRuleIdAsync(ip, ct);
            if (ruleId is null)
            {
                logger.LogDebug("IP {Ip} not found in Cloudflare — nothing to remove", ip);
                return;
            }

            var response = await http.DeleteAsync($"zones/{_opts.ZoneId}/firewall/access_rules/rules/{ruleId}", ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Cloudflare RemoveIp failed for {Ip}: {Status} — {Body}", ip, (int)response.StatusCode, body);
            }
            else
            {
                logger.LogInformation("Cloudflare: removed {Ip}", ip);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloudflare RemoveIp threw for {Ip}", ip);
        }
    }

    private async Task<string?> FindRuleIdAsync(string ip, CancellationToken ct)
    {
        var response = await http.GetAsync(
            $"zones/{_opts.ZoneId}/firewall/access_rules/rules?configuration.target=ip&configuration.value={Uri.EscapeDataString(ip)}&mode=whitelist",
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<CfListResponse>(ct);
        return json?.Result?.FirstOrDefault()?.Id;
    }

    private sealed record CfListResponse([property: JsonPropertyName("result")] List<CfRule>? Result);

    private sealed record CfRule([property: JsonPropertyName("id")] string Id);
}
