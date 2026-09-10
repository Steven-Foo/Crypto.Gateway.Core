using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// Who is making this portal request, from where, and on behalf of which tenant — read from the
/// already-validated session, never from the request body. Mirrors the Back Office's <c>AuditActor</c> so the
/// two hosts write the same shape of entry into the one audit store.
///
/// <para>The tenant is stamped on every entry this produces, which is what keeps one merchant's activity log
/// out of another's (see <see cref="AuditScope"/>). Because it comes from
/// <see cref="PortalTenant.MerchantId"/>, a merchant cannot forge an entry against a tenant it does not own.</para>
/// </summary>
public readonly record struct PortalAuditActor(Guid MerchantUserId, Guid MerchantId, string Username, string? IpAddress)
{
    public static PortalAuditActor From(HttpContext http)
    {
        var principal = PortalTenant.Principal(http);
        return new PortalAuditActor(
            principal.MerchantUserId, principal.MerchantId, principal.Username,
            http.Connection.RemoteIpAddress?.ToString());
    }

    /// <summary>Builds the audit command for one action. <paramref name="entityId"/> identifies the thing
    /// acted on (a role id, an account id); <paramref name="reason"/> is optional operator context.</summary>
    public LogAuditEntryCommand Entry(string action, string entityType, string? entityId, string? reason = null) =>
        new(MerchantUserId, Username, action, entityType, entityId, reason, IpAddress, MerchantId);
}
