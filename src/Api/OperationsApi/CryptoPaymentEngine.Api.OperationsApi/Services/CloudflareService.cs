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

    // ---- Custom Rules (Rulesets API) — the rule that actually blocks traffic ----
    // IP Access Rules (above) only exempt an IP from Cloudflare's own bot/challenge checks; they do not
    // block anything. This is the one thing that does: a single Custom Rule, scoped to
    // CloudflareOptions.CustomRuleHostname, whose expression names every currently-allowlisted merchant IP.

    private const string CustomRuleDescription = "Merchant IP Allowlist (managed by CGC — do not edit manually)";

    /// <summary>Cloudflare rejects a Custom Rule expression around 4096 characters. Warn well before that so
    /// someone notices on a quiet log line, not from a failed push during a busy onboarding day.</summary>
    private const int ExpressionWarnLength = 3000;

    /// <summary>Refuse to push past this rather than let Cloudflare's own rejection be the first sign of
    /// trouble — leaves the existing rule (and its IP set) exactly as it was; nothing sent is worse than
    /// something sent that Cloudflare might reject or a rule half-updated by a plan-specific limit we can't
    /// see from here. The DB-level allowlist check still protects real traffic either way (§ ec2-staging.md §7).</summary>
    private const int ExpressionMaxLength = 3800;

    /// <summary>
    /// Rewrites the Custom Rule that blocks any caller of <see cref="CloudflareOptions.CustomRuleHostname"/> whose
    /// IP is not on file for some merchant. Call after every allowlist change with the FULL, current set of every
    /// merchant's allowed IPs (<c>IMerchantRepository.GetAllAllowedIpsAsync</c>) — the rule's single expression has
    /// to name all of them at once, not just the merchant that just changed.
    ///
    /// <para><b>Two modes, chosen by config, no call-site change:</b> when <see cref="CloudflareOptions.AccountId"/>
    /// and <see cref="CloudflareOptions.IpListName"/> are both set, IPs are synced into a Cloudflare account-level
    /// **List** (<see cref="SyncIpListItemsAsync"/>) and the rule's expression becomes the short, FIXED-length
    /// <c>ip.src in $list_name</c> — no per-expression size ceiling, this is the real fix. Otherwise it falls back
    /// to inlining the IPs directly into the expression (<see cref="ExpressionWarnLength"/>/<see cref="ExpressionMaxLength"/>
    /// guard that path only, since it's the one with a ceiling).</para>
    ///
    /// <para><b>Safety rules that hold in both modes:</b> (1) an empty <paramref name="allowedIps"/> is refused
    /// rather than pushed — an empty allowed-set makes "not in it" true for every caller, blocking all traffic the
    /// instant the merchant table is empty or a query hiccups. (2) this method never flips the Custom Rule from
    /// disabled to enabled. A brand-new rule is created disabled; a human enables it deliberately in the Cloudflare
    /// dashboard once they've confirmed the IP set is right. So a bug here can, at worst, leave the rule stale — it
    /// can never suddenly cut off live traffic on its own.</para>
    /// </summary>
    public async Task SyncCustomRuleAllowlistAsync(IReadOnlyList<string> allowedIps, CancellationToken ct = default)
    {
        if (!_opts.Enabled || string.IsNullOrWhiteSpace(_opts.CustomRuleHostname)) return;

        if (allowedIps.Count == 0)
        {
            logger.LogWarning(
                "Cloudflare custom-rule sync skipped: no merchant has any allowed IP on file — refusing to push " +
                "a rule that would block every caller of {Hostname}", _opts.CustomRuleHostname);
            return;
        }

        var useList = !string.IsNullOrWhiteSpace(_opts.AccountId) && !string.IsNullOrWhiteSpace(_opts.IpListName);

        try
        {
            string expression;
            if (useList)
            {
                if (!await SyncIpListItemsAsync(allowedIps, ct))
                    return; // already logged — Custom Rule left untouched, same "nothing sent beats something wrong" rule as below.

                expression = $"(http.host eq \"{_opts.CustomRuleHostname}\" and not ip.src in ${_opts.IpListName})";
            }
            else
            {
                expression = $"(http.host eq \"{_opts.CustomRuleHostname}\" and not ip.src in {{{string.Join(' ', allowedIps)}}})";

                if (expression.Length >= ExpressionMaxLength)
                {
                    logger.LogError(
                        "Cloudflare custom-rule sync REFUSED: expression is {Length} chars ({IpCount} IPs), at or past " +
                        "the safety ceiling of {Max} (Cloudflare's own limit is ~4096). The rule was left unchanged — " +
                        "the DB-level allowlist check still protects real traffic. Configure Cloudflare:AccountId + " +
                        "Cloudflare:IpListName to switch to the Lists-based sync, which has no such ceiling.",
                        expression.Length, allowedIps.Count, ExpressionMaxLength);
                    return;
                }

                if (expression.Length >= ExpressionWarnLength)
                {
                    logger.LogWarning(
                        "Cloudflare custom-rule expression is {Length} chars ({IpCount} IPs) — approaching the " +
                        "~{Max} safety ceiling. Configure Cloudflare:AccountId + Cloudflare:IpListName to switch " +
                        "to the Lists-based sync before it's hit.",
                        expression.Length, allowedIps.Count, ExpressionMaxLength);
                }
            }

            var entrypointResponse = await http.GetAsync(
                $"zones/{_opts.ZoneId}/rulesets/phases/http_request_firewall_custom/entrypoint", ct);

            RulesetResult? ruleset = null;
            if (entrypointResponse.IsSuccessStatusCode)
            {
                var parsed = await entrypointResponse.Content.ReadFromJsonAsync<RulesetEntrypointResponse>(ct);
                ruleset = parsed?.Result;
            }

            var existingRule = ruleset?.Rules?.FirstOrDefault(r => r.Description == CustomRuleDescription);

            HttpResponseMessage response;
            string action;
            if (ruleset is not null && existingRule is not null)
            {
                // Preserve the rule's own action/enabled — this call only ever rewrites the IP set.
                response = await http.PatchAsJsonAsync(
                    $"zones/{_opts.ZoneId}/rulesets/{ruleset.Id}/rules/{existingRule.Id}",
                    new { expression, description = CustomRuleDescription, action = existingRule.Action, enabled = existingRule.Enabled },
                    ct);
                action = "updated";
            }
            else if (ruleset is not null)
            {
                // Ruleset exists, our rule doesn't yet — append it, disabled until a human turns it on.
                response = await http.PostAsJsonAsync(
                    $"zones/{_opts.ZoneId}/rulesets/{ruleset.Id}/rules",
                    new { description = CustomRuleDescription, expression, action = "block", enabled = false },
                    ct);
                action = "created (disabled — enable it manually in the Cloudflare dashboard)";
            }
            else
            {
                // No custom ruleset exists for this zone/phase at all yet — create it with our rule, disabled.
                response = await http.PutAsJsonAsync(
                    $"zones/{_opts.ZoneId}/rulesets/phases/http_request_firewall_custom/entrypoint",
                    new { rules = new[] { new { description = CustomRuleDescription, expression, action = "block", enabled = false } } },
                    ct);
                action = "ruleset created (disabled — enable it manually in the Cloudflare dashboard)";
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Cloudflare custom-rule sync failed: {Status} — {Body}", (int)response.StatusCode, body);
            }
            else
            {
                logger.LogInformation("Cloudflare custom rule {Action} with {Count} allowed IPs for {Hostname}",
                    action, allowedIps.Count, _opts.CustomRuleHostname);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cloudflare custom-rule sync threw");
        }
    }

    private sealed record RulesetEntrypointResponse([property: JsonPropertyName("result")] RulesetResult? Result);

    private sealed record RulesetResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("rules")] List<CustomRule>? Rules);

    private sealed record CustomRule(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("enabled")] bool Enabled);

    // ---- Lists API (account-scoped) — the real fix for the inline-expression size ceiling above ----
    // A List holds the IPs; the Custom Rule just references it (`ip.src in $name`), so the rule's own expression
    // never grows no matter how many merchants/IPs exist. Item replacement is ASYNCHRONOUS: Cloudflare hands back
    // an operation_id and the change lands moments later, so this polls bulk_operations until it settles.

    private static readonly TimeSpan BulkOperationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BulkOperationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Replaces the full contents of the Cloudflare List named <see cref="CloudflareOptions.IpListName"/>
    /// with <paramref name="allowedIps"/>, creating the list first if this is the very first sync. Returns false
    /// (already logged) on any failure — the caller must not touch the Custom Rule's expression in that case, since
    /// the list and the rule would then disagree about which IPs are actually allowed.</summary>
    private async Task<bool> SyncIpListItemsAsync(IReadOnlyList<string> allowedIps, CancellationToken ct)
    {
        var listId = await FindOrCreateIpListAsync(ct);
        if (listId is null) return false; // already logged

        var items = allowedIps.Select(ip => new { ip }).ToArray();
        var putResponse = await http.PutAsJsonAsync($"accounts/{_opts.AccountId}/rules/lists/{listId}/items", items, ct);

        if (!putResponse.IsSuccessStatusCode)
        {
            var body = await putResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("Cloudflare IP list item replace failed: {Status} — {Body}", (int)putResponse.StatusCode, body);
            return false;
        }

        var started = await putResponse.Content.ReadFromJsonAsync<BulkOperationStartResponse>(ct);
        var operationId = started?.Result?.OperationId;
        if (string.IsNullOrEmpty(operationId))
        {
            logger.LogError("Cloudflare IP list item replace returned no operation id — cannot confirm it applied");
            return false;
        }

        return await WaitForBulkOperationAsync(operationId, allowedIps.Count, ct);
    }

    private async Task<string?> FindOrCreateIpListAsync(CancellationToken ct)
    {
        var listResponse = await http.GetAsync($"accounts/{_opts.AccountId}/rules/lists", ct);
        if (listResponse.IsSuccessStatusCode)
        {
            var parsed = await listResponse.Content.ReadFromJsonAsync<CfListsResponse>(ct);
            var existing = parsed?.Result?.FirstOrDefault(l => l.Name == _opts.IpListName);
            if (existing is not null) return existing.Id;
        }

        var createResponse = await http.PostAsJsonAsync(
            $"accounts/{_opts.AccountId}/rules/lists",
            new { kind = "ip", name = _opts.IpListName, description = "Merchant IP allowlist (managed by CGC — do not edit manually)" },
            ct);

        if (!createResponse.IsSuccessStatusCode)
        {
            var body = await createResponse.Content.ReadAsStringAsync(ct);
            logger.LogError("Cloudflare IP list create failed for {Name}: {Status} — {Body}", _opts.IpListName, (int)createResponse.StatusCode, body);
            return null;
        }

        var created = await createResponse.Content.ReadFromJsonAsync<CfListCreateResponse>(ct);
        logger.LogInformation("Cloudflare IP list {Name} created", _opts.IpListName);
        return created?.Result?.Id;
    }

    /// <summary>Polls until Cloudflare reports the item-replace operation complete, bounded by
    /// <see cref="BulkOperationTimeout"/> — this runs inside the ops request path (allowlist save), so it must not
    /// hang indefinitely. A timeout does not mean the write failed; Cloudflare may still finish it moments later.
    /// It means only that this call can't confirm that yet, so — same rule as everywhere else in this class — the
    /// Custom Rule's expression is left untouched rather than pointed at a list that might not be updated yet.</summary>
    private async Task<bool> WaitForBulkOperationAsync(string operationId, int ipCount, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(BulkOperationTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await http.GetAsync($"accounts/{_opts.AccountId}/rules/lists/bulk_operations/{operationId}", ct);
            if (response.IsSuccessStatusCode)
            {
                var parsed = await response.Content.ReadFromJsonAsync<BulkOperationStatusResponse>(ct);
                switch (parsed?.Result?.Status)
                {
                    case "completed":
                        logger.LogInformation("Cloudflare IP list synced with {Count} IPs ({OperationId})", ipCount, operationId);
                        return true;
                    case "failed":
                        logger.LogError("Cloudflare IP list sync failed ({OperationId}): {Error}", operationId, parsed?.Result?.Error);
                        return false;
                }
            }

            await Task.Delay(BulkOperationPollInterval, ct);
        }

        logger.LogWarning(
            "Cloudflare IP list sync timed out waiting for operation {OperationId} to confirm after {Timeout}s — " +
            "Custom Rule left unchanged this pass; it will retry on the next allowlist change",
            operationId, BulkOperationTimeout.TotalSeconds);
        return false;
    }

    private sealed record CfListsResponse([property: JsonPropertyName("result")] List<CfListInfo>? Result);

    private sealed record CfListInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record CfListCreateResponse([property: JsonPropertyName("result")] CfListInfo? Result);

    private sealed record BulkOperationStartResponse([property: JsonPropertyName("result")] BulkOperationStart? Result);

    private sealed record BulkOperationStart([property: JsonPropertyName("operation_id")] string OperationId);

    private sealed record BulkOperationStatusResponse([property: JsonPropertyName("result")] BulkOperationStatus? Result);

    private sealed record BulkOperationStatus(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("error")] string? Error);
}
