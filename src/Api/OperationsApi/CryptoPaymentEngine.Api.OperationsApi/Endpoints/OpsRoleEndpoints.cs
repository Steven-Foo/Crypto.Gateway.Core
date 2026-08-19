using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing role management — DB-defined roles (§ Role) replacing the old fixed Admin/Viewer enum, so new
/// roles can be added without a redeploy. <see cref="OpsPermissions.All"/> is the catalog a role's permission
/// set is assigned from; this module never validates a code against that catalog (an unrecognized code is
/// simply inert — no endpoint checks for it — the same fail-safe-by-default posture used elsewhere rather
/// than adding validation complexity for a typo that can't do anything).
/// </summary>
public static class OpsRoleEndpoints
{
    public static void MapOpsRoleApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/permissions", GetPermissionCatalog).RequirePermission(OpsPermissions.Roles.View);

        app.MapGet("/api/v1/ops/roles", ListAsync).RequirePermission(OpsPermissions.Roles.View);
        app.MapGet("/api/v1/ops/roles/{id:guid}", GetAsync).RequirePermission(OpsPermissions.Roles.View);
        app.MapPost("/api/v1/ops/roles", CreateAsync).RequirePermission(OpsPermissions.Roles.Manage);
        app.MapPut("/api/v1/ops/roles/{id:guid}", UpdateDetailsAsync).RequirePermission(OpsPermissions.Roles.Manage);
        app.MapPut("/api/v1/ops/roles/{id:guid}/permissions", SetPermissionsAsync).RequirePermission(OpsPermissions.Roles.Manage);
        app.MapDelete("/api/v1/ops/roles/{id:guid}", DeleteAsync).RequirePermission(OpsPermissions.Roles.Manage);
    }

    private static IResult GetPermissionCatalog() =>
        Results.Ok(new { isSuccess = true, data = new { permissions = OpsPermissions.All }, error = (string?)null });

    private static async Task<IResult> ListAsync(IRoleService roles, HttpContext http, int page = 1, int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        var (items, total) = await roles.ListAsync(page, pageSize, http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items },
            error = (string?)null,
        });
    }

    private static async Task<IResult> GetAsync(Guid id, IRoleService roles, HttpContext http)
    {
        var result = await roles.GetAsync(id, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> CreateAsync(CreateRoleRequest request, IRoleService roles, IAuditLogger audit, HttpContext http)
    {
        var result = await roles.CreateAsync(request.Name, request.Description, request.PermissionCodes, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "role.created", "Role", result.Value.RoleId.ToString(),
            $"name={result.Value.Name}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> UpdateDetailsAsync(
        Guid id, UpdateRoleDetailsRequest request, IRoleService roles, IAuditLogger audit, HttpContext http)
    {
        var result = await roles.UpdateDetailsAsync(id, request.Name, request.Description, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "role.updated", "Role", id.ToString(), $"name={request.Name}", actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> SetPermissionsAsync(
        Guid id, SetRolePermissionsRequest request, IRoleService roles, IAuditLogger audit, HttpContext http)
    {
        var result = await roles.SetPermissionsAsync(id, request.PermissionCodes, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "role.permissions_changed", "Role", id.ToString(),
            string.Join(',', request.PermissionCodes), actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> DeleteAsync(Guid id, IRoleService roles, IAuditLogger audit, HttpContext http)
    {
        var result = await roles.DeleteAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "role.deleted", "Role", id.ToString(), null, actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { roleId = id }, error = (string?)null });
    }

    private static IResult Fail(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new { isSuccess = false, error = error.Message }, statusCode: status);
    }
}
