using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Security;

/// <summary>
/// The one place every portal endpoint reads its tenant scope from — the authenticated
/// <see cref="MerchantPrincipal"/> the middleware validated onto <c>HttpContext.Items</c>. The
/// <see cref="MerchantId(HttpContext)"/> is the ONLY source of tenant scope; an endpoint must pass it to every
/// query and must never accept a merchant id from the request (§ tenant-isolation).
/// </summary>
public static class PortalTenant
{
    public static MerchantPrincipal Principal(HttpContext http) =>
        (MerchantPrincipal)http.Items[MerchantSessionAuthMiddleware.PrincipalItem]!;

    public static Guid MerchantId(HttpContext http) => Principal(http).MerchantId;
}
