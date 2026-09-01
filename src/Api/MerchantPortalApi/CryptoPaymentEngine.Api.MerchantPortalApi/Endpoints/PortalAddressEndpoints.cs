using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>The signed-in merchant's deposit addresses, per chain — scoped to the session's tenant. Read-only:
/// the portal shows the addresses; provisioning happens on the payment path, not here.</summary>
public static class PortalAddressEndpoints
{
    public static void MapPortalAddressApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/portal/addresses", GetAsync).RequirePortalPermission(PortalPermissions.Overview.View);

    private static async Task<IResult> GetAsync(
        IWalletDirectory wallets, IAssetCatalog assets, HttpContext http)
    {
        var merchantId = PortalTenant.MerchantId(http);

        // The chains the platform actually supports, from the asset catalog — so we only query wallets on chains
        // that exist (USDT-TRON today).
        var catalog = await assets.GetActiveAsync(http.RequestAborted);
        var chains = catalog.Select(a => a.Chain).Distinct();

        var rows = new List<object>();
        foreach (var chain in chains)
        {
            var walletsOnChain = await wallets.ListAssignedWalletsAsync(merchantId, chain, http.RequestAborted);
            foreach (var wallet in walletsOnChain)
                rows.Add(new { walletId = wallet.WalletId, network = chain.ToString(), address = wallet.Address });
        }

        return Results.Ok(new { isSuccess = true, data = new { items = rows }, error = (string?)null });
    }
}
