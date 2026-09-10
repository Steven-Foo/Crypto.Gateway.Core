using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Api.MerchantPortalApi.Models;
using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The merchant's own staff accounts and roles — tenant-scoped RBAC management. Every call passes the SESSION's
/// merchant id into the service, so a merchant admin can only ever see or change accounts and roles inside its
/// own tenant; an id from another merchant reads as "not found" rather than being actionable.
///
/// <para>A generated one-time password is returned exactly once, on create and on reset — an admin never
/// chooses another user's password, and the value is never stored recoverably or logged (§10, §20).</para>
/// </summary>
public static class PortalAccountEndpoints
{
    public static void MapPortalAccountApi(this IEndpointRouteBuilder app)
    {
        // Accounts
        app.MapGet("/api/v1/portal/accounts", ListAccountsAsync).RequirePortalPermission(PortalPermissions.Accounts.View);
        app.MapPost("/api/v1/portal/accounts", CreateAccountAsync).RequirePortalPermission(PortalPermissions.Accounts.Manage);
        app.MapPatch("/api/v1/portal/accounts/{id:guid}/status", SetAccountStatusAsync).RequirePortalPermission(PortalPermissions.Accounts.Manage);
        app.MapPatch("/api/v1/portal/accounts/{id:guid}/role", AssignAccountRoleAsync).RequirePortalPermission(PortalPermissions.Accounts.Manage);
        app.MapPost("/api/v1/portal/accounts/{id:guid}/reset-password", ResetAccountPasswordAsync).RequirePortalPermission(PortalPermissions.Accounts.Manage);

        // The signed-in user's own password — no permission code: everyone may change their own.
        app.MapPost("/api/v1/portal/account/change-password", ChangeOwnPasswordAsync);

        // Roles
        app.MapGet("/api/v1/portal/roles", ListRolesAsync).RequirePortalPermission(PortalPermissions.Roles.View);
        app.MapPost("/api/v1/portal/roles", CreateRoleAsync).RequirePortalPermission(PortalPermissions.Roles.Manage);
        app.MapPut("/api/v1/portal/roles/{id:guid}", UpdateRoleAsync).RequirePortalPermission(PortalPermissions.Roles.Manage);
        app.MapPut("/api/v1/portal/roles/{id:guid}/permissions", SetRolePermissionsAsync).RequirePortalPermission(PortalPermissions.Roles.Manage);
        app.MapDelete("/api/v1/portal/roles/{id:guid}", DeleteRoleAsync).RequirePortalPermission(PortalPermissions.Roles.Manage);

        // The catalog a Roles editor assigns from — any authenticated portal user may read it.
        app.MapGet("/api/v1/portal/permissions", () => Ok(new { permissions = PortalPermissions.All }));
    }

    // ── accounts ──

    private static async Task<IResult> ListAccountsAsync(IMerchantAccountService accounts, HttpContext http)
    {
        var result = await accounts.ListAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        return result.IsFailure ? Fail(result.Error!) : Ok(new { items = result.Value });
    }

    private static async Task<IResult> CreateAccountAsync(
        CreatePortalAccountRequest request, IMerchantAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.CreateAsync(
            PortalTenant.MerchantId(http), request.Username, request.DisplayName ?? "", request.RoleId, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        // The username and assigned role are recorded; the generated password NEVER is (§10 — a credential
        // must not be recoverable from an audit trail, which is read by more people than issued it).
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.AccountCreated, PortalAuditActions.EntityAccount,
                result.Value.MerchantUserId.ToString(),
                $"username={result.Value.Username}; roleId={request.RoleId?.ToString() ?? "none"}"),
            http.RequestAborted);

        return Ok(new
        {
            merchantUserId = result.Value.MerchantUserId,
            username = result.Value.Username,
            // Shown ONCE. The UI must present this as copy-now-never-again (§20).
            temporaryPassword = result.Value.TemporaryPassword,
        });
    }

    private static async Task<IResult> SetAccountStatusAsync(
        Guid id, SetPortalAccountStatusRequest request, IMerchantAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var principal = PortalTenant.Principal(http);
        var result = await accounts.SetStatusAsync(
            principal.MerchantId, id, principal.MerchantUserId, request.Active, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.AccountStatusChanged, PortalAuditActions.EntityAccount, id.ToString(),
                request.Active ? "enabled" : "disabled"),
            http.RequestAborted);

        return Ok(new { merchantUserId = id, active = request.Active });
    }

    private static async Task<IResult> AssignAccountRoleAsync(
        Guid id, AssignPortalRoleRequest request, IMerchantAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.AssignRoleAsync(PortalTenant.MerchantId(http), id, request.RoleId, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.AccountRoleChanged, PortalAuditActions.EntityAccount, id.ToString(),
                $"roleId={request.RoleId?.ToString() ?? "none"}"),
            http.RequestAborted);

        return Ok(new { merchantUserId = id, roleId = request.RoleId });
    }

    private static async Task<IResult> ResetAccountPasswordAsync(
        Guid id, IMerchantAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.ResetPasswordAsync(PortalTenant.MerchantId(http), id, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        // That a reset happened, and by whom — never the issued password itself.
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.AccountPasswordReset, PortalAuditActions.EntityAccount, id.ToString(),
                $"username={result.Value.Username}"),
            http.RequestAborted);

        return Ok(new
        {
            merchantUserId = result.Value.MerchantUserId,
            username = result.Value.Username,
            temporaryPassword = result.Value.TemporaryPassword, // shown once
        });
    }

    private static async Task<IResult> ChangeOwnPasswordAsync(
        ChangeOwnPasswordRequest request, IMerchantAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var principal = PortalTenant.Principal(http);
        var result = await accounts.ChangeOwnPasswordAsync(
            principal.MerchantId, principal.MerchantUserId, request.CurrentPassword, request.NewPassword, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.OwnPasswordChanged, PortalAuditActions.EntityAccount,
                principal.MerchantUserId.ToString()),
            http.RequestAborted);

        return Ok(new { changed = true });
    }

    // ── roles ──

    private static async Task<IResult> ListRolesAsync(IMerchantRoleService roles, HttpContext http)
    {
        var result = await roles.ListAsync(PortalTenant.MerchantId(http), http.RequestAborted);
        return result.IsFailure ? Fail(result.Error!) : Ok(new { items = result.Value });
    }

    private static async Task<IResult> CreateRoleAsync(
        CreatePortalRoleRequest request, IMerchantRoleService roles, IAuditLogger audit, HttpContext http)
    {
        if (!PortalPermissions.AreAllKnown(request.PermissionCodes, out var unknown))
            return Bad(PortalErrorCodes.InvalidPermissionCode, $"Unknown permission code '{unknown}'.");

        var result = await roles.CreateAsync(
            PortalTenant.MerchantId(http), request.Name, request.Description, request.PermissionCodes, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        // Audited only on success — a rejected attempt is not an action, and recording one would make the
        // trail unusable as evidence of what actually changed. The granted codes are the point of the entry,
        // so they go in the reason rather than being left to a "compare adjacent rows" reconstruction.
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.RoleCreated, PortalAuditActions.EntityRole, result.Value.RoleId.ToString(),
                $"name={request.Name}; permissions=[{string.Join(' ', request.PermissionCodes)}]"),
            http.RequestAborted);

        return Ok(result.Value);
    }

    private static async Task<IResult> UpdateRoleAsync(
        Guid id, UpdatePortalRoleRequest request, IMerchantRoleService roles, IAuditLogger audit, HttpContext http)
    {
        var result = await roles.UpdateAsync(
            PortalTenant.MerchantId(http), id, request.Name, request.Description, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.RoleUpdated, PortalAuditActions.EntityRole, id.ToString(),
                $"name={request.Name}"),
            http.RequestAborted);

        return Ok(result.Value);
    }

    private static async Task<IResult> SetRolePermissionsAsync(
        Guid id, SetPortalRolePermissionsRequest request, IMerchantRoleService roles, IAuditLogger audit, HttpContext http)
    {
        // Validated against the portal catalog at the edge, so a tenant can never store a code the portal does
        // not define — including a platform ops.* code (§ PortalPermissions).
        if (!PortalPermissions.AreAllKnown(request.PermissionCodes, out var unknown))
            return Bad(PortalErrorCodes.InvalidPermissionCode, $"Unknown permission code '{unknown}'.");

        var result = await roles.SetPermissionsAsync(
            PortalTenant.MerchantId(http), id, request.PermissionCodes, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        // The highest-value entry in this log: a permission change is how a portal user gains the ability to
        // move money, so the resulting code set is recorded verbatim.
        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.RolePermissionsChanged, PortalAuditActions.EntityRole, id.ToString(),
                $"permissions=[{string.Join(' ', request.PermissionCodes)}]"),
            http.RequestAborted);

        return Ok(result.Value);
    }

    private static async Task<IResult> DeleteRoleAsync(
        Guid id, IMerchantRoleService roles, IAuditLogger audit, HttpContext http)
    {
        var result = await roles.DeleteAsync(PortalTenant.MerchantId(http), id, http.RequestAborted);

        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(
            PortalAuditActor.From(http).Entry(
                PortalAuditActions.RoleDeleted, PortalAuditActions.EntityRole, id.ToString()),
            http.RequestAborted);

        return Ok(new { roleId = id, deleted = true });
    }

    // ── helpers ──

    // Thin delegations to the host-wide mapper (§7.1), so every response on this host carries an errorCode.
    private static IResult Ok(object data) => PortalResults.Ok(data);

    private static IResult Fail(Error error) => PortalResults.Fail(error);

    private static IResult Bad(string errorCode, string message) => PortalResults.Bad(errorCode, message);
}
