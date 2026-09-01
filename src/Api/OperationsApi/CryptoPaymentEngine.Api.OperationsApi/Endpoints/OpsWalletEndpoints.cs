using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
using CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing wallet browsing and holds. Suspend/resume place or lift a temporary hold on one deposit
/// address (e.g. it received an unexpected/off-flow transfer and is being held for investigation) — never
/// a permanent decommission, and never touches the merchant assignment (see <c>Wallet.Suspend</c>).
/// </summary>
public static class OpsWalletEndpoints
{
    public static void MapOpsWalletApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/wallets", SearchAsync).RequirePermission(OpsPermissions.Wallets.View);
        app.MapGet("/api/v1/ops/wallets/{id:guid}", GetAsync).RequirePermission(OpsPermissions.Wallets.View);
        app.MapPost("/api/v1/ops/wallets/{id:guid}/suspend", SuspendAsync).RequirePermission(OpsPermissions.Wallets.Manage);
        app.MapPost("/api/v1/ops/wallets/{id:guid}/resume", ResumeAsync).RequirePermission(OpsPermissions.Wallets.Manage);
    }

    private static async Task<IResult> SearchAsync(
        IWalletAdminService wallets,
        IMerchantDirectory merchants,
        HttpContext http,
        Guid? merchantId = null,
        string? address = null,
        Chain? chain = null,
        string? status = null,
        string? walletType = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        WalletStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WalletStatus>(status, ignoreCase: true, out var parsed))
                return OpsResults.Bad(OpsErrorCodes.InvalidStatus, $"Unknown status '{status}'. Expected Active, Disabled, or Suspended.");

            statusFilter = parsed;
        }

        // Rejected rather than ignored: silently dropping an unrecognised walletType would widen the result
        // set to every wallet while the operator believes they are looking at one kind.
        WalletType? walletTypeFilter = null;
        if (!string.IsNullOrWhiteSpace(walletType))
        {
            if (!Enum.TryParse<WalletType>(walletType, ignoreCase: true, out var parsedType))
                return OpsResults.Bad(OpsErrorCodes.InvalidWalletType, $"Unknown walletType '{walletType}'. Expected one of: {string.Join(", ", Enum.GetNames<WalletType>())}.");

            walletTypeFilter = parsedType;
        }

        var filter = new WalletAdminFilter(
            merchantId, address?.Trim(), chain, statusFilter, WalletType: walletTypeFilter);
        var (items, total) = await wallets.SearchAsync(filter, page, pageSize, http.RequestAborted);

        var rows = await BuildRowsAsync(items, merchants, http);
        return OpsResults.Ok(new { page, pageSize, totalCount = total, items = rows });
    }

    /// <summary>
    /// One wallet by id (REQ-6) — the deep-linkable detail view. Returns the <b>identical row shape</b> the
    /// list returns, built by the very same projection, so the table and the record it opens can never
    /// disagree about a field.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid id, IWalletAdminService wallets, IMerchantDirectory merchants, HttpContext http)
    {
        // Reuses the search filter rather than adding a by-id Contract method: WalletId is a unique
        // narrowing, so this returns exactly zero or one row.
        var (items, _) = await wallets.SearchAsync(
            new WalletAdminFilter(null, null, null, null, WalletId: id), 1, 1, http.RequestAborted);

        if (items.Count == 0)
            return OpsResults.NotFound(OpsErrorCodes.NotFound, $"No wallet found with id '{id}'.");

        var rows = await BuildRowsAsync(items, merchants, http);
        return OpsResults.Ok(rows[0]);
    }

    /// <summary>The single wallet-row projection, shared by the list and the detail endpoint.</summary>
    private static async Task<List<object>> BuildRowsAsync(
        IReadOnlyList<WalletAdminRow> items, IMerchantDirectory merchants, HttpContext http)
    {
        // MerchantId is nullable here — platform wallets (hot pool, staking, treasury, etc.) have none, only
        // deposit wallets are ever assigned to a merchant.
        var merchantNames = await merchants.GetNamesByIdsAsync(
            items.Where(w => w.MerchantId is not null).Select(w => w.MerchantId!.Value).Distinct().ToList(),
            http.RequestAborted);

        return items.Select(object (w) => new
        {
            w.WalletId,
            w.MerchantId,
            merchantName = w.MerchantId is { } id ? merchantNames.GetValueOrDefault(id) : null,
            w.Chain,
            w.Address,
            w.WalletType,
            w.Status,
            w.StatusReason,
            w.DepositsReceivedCount,
            w.CreatedAt,
            w.UpdatedAt,
        }).ToList();
    }

    private static async Task<IResult> SuspendAsync(
        Guid id, SuspendWalletRequest request, IWalletAdminService wallets, IAuditLogger audit, HttpContext http)
    {
        var result = await wallets.SuspendAsync(new SuspendWalletCommand(id, request.Reason), http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "wallet.suspended", "Wallet", id.ToString(),
            request.Reason, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { walletId = id, status = "Suspended" }, error = (string?)null, errorCode = (string?)null });
    }

    private static async Task<IResult> ResumeAsync(
        Guid id, IWalletAdminService wallets, IAuditLogger audit, HttpContext http)
    {
        var result = await wallets.ResumeAsync(new ResumeWalletCommand(id), http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "wallet.resumed", "Wallet", id.ToString(),
            null, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { walletId = id, status = "Active" }, error = (string?)null, errorCode = (string?)null });
    }
}
