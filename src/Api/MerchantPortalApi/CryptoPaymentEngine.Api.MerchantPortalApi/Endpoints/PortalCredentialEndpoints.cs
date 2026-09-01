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
    private static async Task<IResult> RotateAsync(IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.RotateCredentialAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        return Ok(new
        {
            apiKey = result.Value.ApiKey,
            apiSecret = result.Value.ApiSecret,
            signingSecret = result.Value.SigningSecret,
        });
    }

    private static async Task<IResult> UpdateAllowedIpsAsync(
        UpdateAllowedIpsRequest request, IMerchantRegistrar registrar, HttpContext http)
    {
        // IP format validation is the host edge's job (the module only persists and diffs). Reject malformed
        // input rather than storing an entry that would silently never match.
        var entries = request.AllowedIps.Select(ip => ip.Trim()).Where(ip => ip.Length > 0).ToList();
        foreach (var entry in entries)
        {
            if (!IsValidIpOrCidr(entry))
                return Bad($"'{entry}' is not a valid IP address or CIDR range.");
        }

        var result = await registrar.UpdateAllowedIpsAsync(PortalTenant.MerchantId(http), entries, http.RequestAborted);
        return result.IsFailure ? Fail(result.Error!) : Ok(new { allowedIps = entries });
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

    private static IResult Ok(object data) =>
        Results.Ok(new { isSuccess = true, data, error = (string?)null });

    private static IResult Fail(Error error) =>
        Results.Json(
            new { isSuccess = false, error = error.Message },
            statusCode: error.Type switch
            {
                ErrorType.NotFound => StatusCodes.Status404NotFound,
                ErrorType.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            });

    private static IResult Bad(string message) =>
        Results.Json(new { isSuccess = false, error = message }, statusCode: StatusCodes.Status400BadRequest);
}
