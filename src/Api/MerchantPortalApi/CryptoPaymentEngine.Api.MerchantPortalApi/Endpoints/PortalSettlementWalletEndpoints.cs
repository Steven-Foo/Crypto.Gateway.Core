using System.ComponentModel.DataAnnotations;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

public sealed class SubmitSettlementWalletRequest
{
    /// <summary>The network, e.g. <c>Tron</c>.</summary>
    [Required, MaxLength(16)] public string Network { get; init; } = null!;

    [Required, MaxLength(128)] public string Address { get; init; } = null!;

    /// <summary>The merchant's own name for the address, so several can be told apart.</summary>
    [MaxLength(128)] public string? Label { get; init; }
}

/// <summary>
/// The merchant's own cash-out (settlement) addresses (2026-10-14).
///
/// <para><b>The merchant can propose, never approve.</b> A submitted address is <c>PendingApproval</c> and can't
/// receive money until platform staff approve it in the admin BO — so a stolen merchant login can request a new
/// destination but can never point earnings at one on its own (§10). Once approved, the merchant may switch which
/// APPROVED address is active; it can never activate a pending, rejected or cancelled one. Tenant-scoped: the
/// merchant id comes only from the session.</para>
/// </summary>
public static class PortalSettlementWalletEndpoints
{
    public static void MapPortalSettlementWalletApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/portal/settlement-wallets", ListAsync)
            .RequirePortalPermission(PortalPermissions.Settlement.View);

        app.MapPost("/api/v1/portal/settlement-wallets", SubmitAsync)
            .RequirePortalPermission(PortalPermissions.Settlement.Manage)
            .RequirePortalTwoFactor(MerchantGuardedActions.SettlementWalletManage);

        app.MapPost("/api/v1/portal/settlement-wallets/{walletId:guid}/cancel", CancelAsync)
            .RequirePortalPermission(PortalPermissions.Settlement.Manage)
            .RequirePortalTwoFactor(MerchantGuardedActions.SettlementWalletManage);

        app.MapPost("/api/v1/portal/settlement-wallets/{walletId:guid}/activate", ActivateAsync)
            .RequirePortalPermission(PortalPermissions.Settlement.Manage)
            .RequirePortalTwoFactor(MerchantGuardedActions.SettlementWalletManage);
    }

    private static async Task<IResult> ListAsync(IMerchantRegistrar registrar, HttpContext http)
    {
        var result = await registrar.ListSettlementWalletsAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        return result.IsFailure ? PortalResults.Fail(result.Error!) : PortalResults.Ok(new { items = result.Value.Select(ToRow) });
    }

    private static async Task<IResult> SubmitAsync(
        SubmitSettlementWalletRequest request, IMerchantRegistrar registrar, IAddressEncoderFactory encoders,
        IAuditLogger audit, HttpContext http)
    {
        if (!Enum.TryParse<Chain>(request.Network, ignoreCase: true, out var chain) || int.TryParse(request.Network, out _))
            return PortalResults.Bad(PortalErrorCodes.InvalidChain, $"Unknown network '{request.Network}'.");

        if (string.IsNullOrWhiteSpace(request.Address))
            return PortalResults.Bad(PortalErrorCodes.AddressRequired, "address is required.");

        var address = request.Address.Trim();

        // A typo here would only be caught by staff at review — or not at all. Refuse a malformed address up front.
        if (encoders.Supports(chain) && !encoders.For(chain).IsValidAddress(address))
            return PortalResults.Bad(PortalErrorCodes.InvalidAddress, $"'{address}' is not a valid {chain} address.");

        var principal = PortalTenant.Principal(http);
        var result = await registrar.SubmitSettlementWalletAsync(
            principal.MerchantId, chain, address, request.Label, principal.Username, http.RequestAborted);
        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.SettlementWalletSubmitted, PortalAuditActions.EntitySettlementWallet,
                result.Value.WalletId.ToString(), $"{chain} {address}"),
            http.RequestAborted);

        return PortalResults.Ok(Describe(result.Value));
    }

    private static async Task<IResult> CancelAsync(
        Guid walletId, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        var result = await registrar.CancelSettlementWalletAsync(PortalTenant.MerchantId(http), walletId, http.RequestAborted);
        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.SettlementWalletCancelled, PortalAuditActions.EntitySettlementWallet,
                walletId.ToString(), $"{result.Value.Chain} {result.Value.Address}"),
            http.RequestAborted);

        return PortalResults.Ok(Describe(result.Value));
    }

    /// <summary>Switches the merchant's cash-out address to another APPROVED one; the current active one goes back
    /// to standby. Refused for an address that isn't approved, or whose recorded screening found it sanctioned.</summary>
    private static async Task<IResult> ActivateAsync(
        Guid walletId, IMerchantRegistrar registrar, IAuditLogger audit, HttpContext http)
    {
        // screenNow: false — this host has no screening module; the registrar uses the verdict stored on the
        // wallet when staff approved it, and still refuses a sanctioned address.
        var result = await registrar.ActivateSettlementWalletAsync(
            PortalTenant.MerchantId(http), walletId, http.RequestAborted, screenNow: false);
        if (result.IsFailure)
            return PortalResults.Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.SettlementWalletActivated, PortalAuditActions.EntitySettlementWallet,
                walletId.ToString(), $"{result.Value.Chain} {result.Value.Address}"),
            http.RequestAborted);

        return PortalResults.Ok(Describe(result.Value));
    }

    private static object ToRow(MerchantSettlementWalletView w) => new
    {
        w.WalletId,
        network = w.Chain,
        w.Address,
        w.Label,
        w.Status,
        isActive = w.Status == "Active",
        w.Source,
        w.SubmittedBy,
        w.ReviewedBy,
        w.ReviewedAt,
        w.RejectionReason,
        w.CreatedAt,
        w.UpdatedAt,
    };

    private static object Describe(SettlementWalletResult r) => new
    {
        walletId = r.WalletId,
        network = r.Chain.ToString(),
        address = r.Address,
        label = r.Label,
        status = r.Status,
        isActive = r.Status == "Active",
    };
}
