using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing audit log search — who did what, to what, and when across the Back Office. Read-only: audit
/// entries are never created through this API, only ever written internally by
/// <see cref="IAuditLogger"/> right after a mutating Ops endpoint succeeds (§ AuditService).
/// </summary>
public static class OpsAuditEndpoints
{
    public static void MapOpsAuditApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/audit", SearchAsync).RequirePermission(OpsPermissions.Audit.View);

    private static async Task<IResult> SearchAsync(
        IAuditQuery audit,
        HttpContext http,
        Guid? staffUserId = null,
        string? action = null,
        string? entityType = null,
        string? entityId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        var filter = new AuditSearchFilter(staffUserId, action, entityType, entityId, fromDate, toDate);
        var (items, total) = await audit.SearchAsync(filter, page, pageSize, http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items },
            error = (string?)null,
        });
    }
}
