using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Security;

/// <summary>Who's making this request and from where — pulled from the already-validated session, plus the
/// caller's IP. The one place every mutating endpoint reads this from before writing an audit entry, so the
/// 19-odd call sites stay a one-liner instead of repeating the same three lines each.</summary>
public readonly record struct AuditActor(Guid StaffUserId, string Username, string? IpAddress)
{
    public static AuditActor From(HttpContext http)
    {
        var principal = (StaffPrincipal)http.Items[StaffBearerAuthMiddleware.PrincipalItem]!;
        return new AuditActor(principal.StaffUserId, principal.Username, http.Connection.RemoteIpAddress?.ToString());
    }
}
