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

    /// <summary>
    /// Paged, because a merchant's address count grows with its deposit history — this list is unbounded over
    /// time, unlike accounts or roles which are bounded by headcount.
    ///
    /// <para>Paging is per <paramref name="network"/>. Left unspecified, the response covers every supported
    /// chain and reports each chain's own total, because there is no single meaningful ordering across chains
    /// to page through — inventing one would produce a page number that means different things depending on
    /// how many chains happen to be active. Today only TRON is live, so the common case is one group.</para>
    /// </summary>
    private static async Task<IResult> GetAsync(
        IWalletDirectory wallets,
        IAssetCatalog assets,
        HttpContext http,
        string? network = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        pageSize = pageSize switch { < 1 => 50, > 200 => 200, _ => pageSize };

        var merchantId = PortalTenant.MerchantId(http);

        // The chains the platform actually supports, from the asset catalog — so we only query wallets on chains
        // that exist (USDT-TRON today).
        var catalog = await assets.GetActiveAsync(http.RequestAborted);
        var chains = catalog.Select(a => a.Chain).Distinct().ToList();

        if (!string.IsNullOrWhiteSpace(network))
        {
            if (!Enum.TryParse<Chain>(network, ignoreCase: true, out var requested) || !chains.Contains(requested))
                return PortalResults.Bad(
                    PortalErrorCodes.InvalidChain, $"Unknown or unsupported network '{network}'.");

            chains = [requested];
        }

        var groups = new List<AddressGroup>();
        var grandTotal = 0;

        foreach (var chain in chains)
        {
            var (items, total) = await wallets.SearchAssignedWalletsAsync(
                merchantId, chain, page, pageSize, http.RequestAborted);

            grandTotal += total;
            groups.Add(new AddressGroup(
                chain.ToString(),
                total,
                [.. items.Select(w => new AddressRow(w.WalletId, chain.ToString(), w.Address))]));
        }

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                page,
                pageSize,
                // Across every chain in this response — so a UI can tell "no addresses at all" from "none on
                // this page". Each group carries its own total, which is what a per-chain pager needs.
                totalCount = grandTotal,
                networks = groups,
                // The flat list the pre-paging response returned, preserved so existing callers keep working.
                // It holds this page's rows across the chains above, not every address the merchant owns.
                items = groups.SelectMany(g => g.Items),
            },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }
}

/// <summary>JSON shapes for the address list. Named records rather than anonymous objects so the flat
/// backward-compatible <c>items</c> projection is type-checked, not reflected over at runtime.</summary>
internal sealed record AddressRow(Guid WalletId, string Network, string Address);

internal sealed record AddressGroup(string Network, int TotalCount, IReadOnlyList<AddressRow> Items);
