using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing account management — who can log into the Back Office at all, and which role they hold.
/// Passwords are never accepted from the caller: creation and reset both generate a strong random one,
/// returned exactly once (§10-adjacent, same one-time-secret convention as merchant credentials).
/// </summary>
public static class OpsAccountEndpoints
{
    public static void MapOpsAccountApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ops/accounts", ListAsync).RequirePermission(OpsPermissions.Accounts.View);
        app.MapGet("/api/v1/ops/accounts/{id:guid}", GetAsync).RequirePermission(OpsPermissions.Accounts.View);

        app.MapPost("/api/v1/ops/accounts", CreateAsync).RequirePermission(OpsPermissions.Accounts.Manage);
        app.MapPatch("/api/v1/ops/accounts/{id:guid}/status", SetStatusAsync).RequirePermission(OpsPermissions.Accounts.Manage);
        app.MapPatch("/api/v1/ops/accounts/{id:guid}/role", ChangeRoleAsync).RequirePermission(OpsPermissions.Accounts.Manage);
        app.MapPost("/api/v1/ops/accounts/{id:guid}/reset-password", ResetPasswordAsync).RequirePermission(OpsPermissions.Accounts.Manage);
    }

    private static async Task<IResult> ListAsync(IStaffAccountService accounts, HttpContext http, int page = 1, int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        var (items, total) = await accounts.ListAsync(page, pageSize, http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items },
            error = (string?)null,
        });
    }

    private static async Task<IResult> GetAsync(Guid id, IStaffAccountService accounts, HttpContext http)
    {
        var result = await accounts.GetAsync(id, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> CreateAsync(
        CreateAccountRequest request, IStaffAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.CreateAsync(request.Username, request.RoleId, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "account.created", "StaffUser", result.Value.StaffUserId.ToString(),
            $"username={result.Value.Username}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                staffUserId = result.Value.StaffUserId,
                username = result.Value.Username,
                password = result.Value.Password,
                warning = "Store this password securely — it will never be shown again.",
            },
            error = (string?)null,
        });
    }

    private static async Task<IResult> SetStatusAsync(
        Guid id, SetAccountStatusRequest request, IStaffAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var actor = AuditActor.From(http);
        var result = await accounts.SetStatusAsync(id, request.Active, actor.StaffUserId, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "account.status_changed", "StaffUser", id.ToString(),
            $"active={request.Active}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> ChangeRoleAsync(
        Guid id, ChangeAccountRoleRequest request, IStaffAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.ChangeRoleAsync(id, request.RoleId, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "account.role_changed", "StaffUser", id.ToString(),
            $"roleId={request.RoleId}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = result.Value, error = (string?)null });
    }

    private static async Task<IResult> ResetPasswordAsync(Guid id, IStaffAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.ResetPasswordAsync(id, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "account.password_reset", "StaffUser", id.ToString(), null, actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                staffUserId = result.Value.StaffUserId,
                username = result.Value.Username,
                password = result.Value.Password,
                warning = "Store this password securely — it will never be shown again.",
            },
            error = (string?)null,
        });
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
