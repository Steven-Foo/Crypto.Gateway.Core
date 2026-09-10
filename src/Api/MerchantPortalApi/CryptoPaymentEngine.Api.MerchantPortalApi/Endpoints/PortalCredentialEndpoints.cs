using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using System.Net;
using CryptoPaymentEngine.Api.MerchantPortalApi.Models;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The merchant's own API credential and IP allowlist — the self-service half of what staff can do from Ops.
/// Everything is scoped to the SESSION's merchant id, never a request parameter.
///
/// <para><b>Deliberately absent: the settlement wallet.</b> A merchant cannot set its own cash-out destination
/// here. That address is whitelisted by platform staff precisely so a compromised merchant credential (or
/// portal session) cannot redirect the merchant's earnings (§10). It stays an Ops-only action.</para>
/// </summary>
public static class PortalCredentialEndpoints
{
    public static void MapPortalCredentialApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/portal/api-credential", GetAsync).RequirePortalPermission(PortalPermissions.ApiCredentials.View);
        app.MapPost("/api/v1/portal/api-credential/rotate", RotateAsync).RequirePortalPermission(PortalPermissions.ApiCredentials.Manage);
        app.MapPut("/api/v1/portal/allowed-ips", UpdateAllowedIpsAsync).RequirePortalPermission(PortalPermissions.ApiCredentials.Manage);
    }

    /// <summary>Credential <em>metadata</em> only — whether an active credential exists and the current IP
    /// allowlist. Never the API key or any secret: those are readable exactly once, at rotation.</summary>
    private static async Task<IResult> GetAsync(IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.GetAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        return Ok(new
        {
            hasActiveCredential = result.Value.HasActiveCredential,
            allowedIps = result.Value.AllowedIps,
        });
    }

    /// <summary>
    /// Rotates the merchant's API credential: the current one stops working immediately and a fresh key/secret
    /// pair is returned <b>once</b>. The UI must present these as copy-now-never-again and must not persist them
    /// (§20). This is a genuine self-service lockout risk — rotating invalidates the secret the merchant's own
    /// servers are signing with — so the portal should confirm the impact before calling (§16).
    /// </summary>
    private static async Task<IResult> RotateAsync(
        IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.RotateCredentialAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        // Records THAT the credential was rotated and by whom — never the key, secret, or signing secret
        // (§10: secrets never reach a log). This is the entry that explains a sudden wave of 401s from the
        // merchant's own servers, so the timestamp and actor are the whole value.
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.ApiCredentialRotated, PortalAuditActions.EntityCredential, null),
            http.RequestAborted);

        return Ok(new
        {
            apiKey = result.Value.ApiKey,
            apiSecret = result.Value.ApiSecret,
            signingSecret = result.Value.SigningSecret,
        });
    }

    private static async Task<IResult> UpdateAllowedIpsAsync(
        UpdateAllowedIpsRequest request, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        // IP format validation is the host edge's job (the module only persists and diffs). Reject malformed
        // input rather than storing an entry that would silently never match.
        var entries = request.AllowedIps.Select(ip => ip.Trim()).Where(ip => ip.Length > 0).ToList();
        foreach (var entry in entries)
        {
            if (!IsValidIpOrCidr(entry))
                return Bad(PortalErrorCodes.InvalidIpAddress, $"'{entry}' is not a valid IP address or CIDR range.");
        }

        var result = await registrar.UpdateAllowedIpsAsync(PortalTenant.MerchantId(http), entries, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        // The resulting allowlist is recorded in full: this is a security control, and "who opened it up, and
        // to what" is the question the trail exists to answer. An empty list means the allowlist was cleared.
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.AllowedIpsUpdated, PortalAuditActions.EntityMerchant, null,
                entries.Count == 0 ? "cleared" : $"allowedIps=[{string.Join(' ', entries)}]"),
            http.RequestAborted);

        return Ok(new { allowedIps = entries });
    }

    /// <summary>Accepts a bare IP or a CIDR range. Deliberately permissive about which form the merchant uses,
    /// strict about it actually parsing.</summary>
    private static bool IsValidIpOrCidr(string value)
    {
        var slash = value.IndexOf('/');
        if (slash < 0)
            return IPAddress.TryParse(value, out _);

        var address = value[..slash];
        var prefix = value[(slash + 1)..];
        if (!IPAddress.TryParse(address, out var parsed) || !int.TryParse(prefix, out var bits))
            return false;

        var maxBits = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return bits >= 0 && bits <= maxBits;
    }

    // Thin delegations to the host-wide mapper (§7.1), so every response on this host carries an errorCode.
    private static IResult Ok(object data) => PortalResults.Ok(data);

    private static IResult Fail(Error error) => PortalResults.Fail(error);

    private static IResult Bad(string errorCode, string message) => PortalResults.Bad(errorCode, message);
}
