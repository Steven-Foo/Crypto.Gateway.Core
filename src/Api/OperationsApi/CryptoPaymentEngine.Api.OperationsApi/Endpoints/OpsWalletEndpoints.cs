using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Application.Abstractions;
using CryptoPaymentEngine.Gateway.Core.AssetManagement.Wallet.Domain;
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
        app.MapPost("/api/v1/ops/wallets/{id:guid}/suspend", SuspendAsync).RequirePermission(OpsPermissions.Wallets.Manage);
        app.MapPost("/api/v1/ops/wallets/{id:guid}/resume", ResumeAsync).RequirePermission(OpsPermissions.Wallets.Manage);
    }

    private static async Task<IResult> SearchAsync(
        IWalletAdminService wallets,
        HttpContext http,
        Guid? merchantId = null,
        string? address = null,
        Chain? chain = null,
        string? status = null,
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
                return Results.Json(
                    new { isSuccess = false, error = $"Unknown status '{status}'. Expected Active, Disabled, or Suspended." },
                    statusCode: StatusCodes.Status400BadRequest);

            statusFilter = parsed;
        }

        var filter = new WalletAdminFilter(merchantId, address?.Trim(), chain, statusFilter);
        var (items, total) = await wallets.SearchAsync(filter, page, pageSize, http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items },
            error = (string?)null,
        });
    }

    private static async Task<IResult> SuspendAsync(
        Guid id, SuspendWalletRequest request, IWalletAdminService wallets, IAuditLogger audit, HttpContext http)
    {
        var result = await wallets.SuspendAsync(new SuspendWalletCommand(id, request.Reason), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "wallet.suspended", "Wallet", id.ToString(),
            request.Reason, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { walletId = id, status = "Suspended" }, error = (string?)null });
    }

    private static async Task<IResult> ResumeAsync(
        Guid id, IWalletAdminService wallets, IAuditLogger audit, HttpContext http)
    {
        var result = await wallets.ResumeAsync(new ResumeWalletCommand(id), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "wallet.resumed", "Wallet", id.ToString(),
            null, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { walletId = id, status = "Active" }, error = (string?)null });
    }

    private static IResult Fail(Error error)
    {
        var status = error.Type == ErrorType.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status409Conflict;
        return Results.Json(new { isSuccess = false, error = error.Message }, statusCode: status);
    }
}
