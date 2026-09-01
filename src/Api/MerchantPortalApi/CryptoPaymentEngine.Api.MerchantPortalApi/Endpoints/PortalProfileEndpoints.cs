using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>The signed-in merchant's own profile — resolved from the session's tenant id, never a request
/// parameter (§ tenant-isolation). Carries no credential material.</summary>
public static class PortalProfileEndpoints
{
    public static void MapPortalProfileApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/portal/profile", GetAsync).RequirePortalPermission(PortalPermissions.Overview.View);

    private static async Task<IResult> GetAsync(IMerchantDirectory merchants, HttpContext http)
    {
        var merchant = await merchants.FindByIdAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        if (merchant is null)
            return Results.Json(new { isSuccess = false, error = "Merchant not found." }, statusCode: StatusCodes.Status404NotFound);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = merchant.MerchantId,
                merchantCode = merchant.MerchantCode,
                name = merchant.Name,
                callbackUrl = merchant.CallbackUrl,
                canTransact = merchant.CanTransact,
                settlementDelayDays = merchant.SettlementDelayDays,
            },
            error = (string?)null,
        });
    }
}
