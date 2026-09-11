using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Merchant.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.MerchantIdentity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Bootstraps a merchant's <em>first</em> portal login — the gap the merchant portal's own self-service
/// account creation (<c>POST /api/v1/portal/accounts</c>) cannot close, because that endpoint requires an
/// already-authenticated portal session, and a brand-new merchant has none. This host never issues portal
/// sessions itself (that stays <c>MerchantPortalApi</c>'s job) — it only composes
/// <see cref="IMerchantAccountService"/>/<see cref="IMerchantRoleService"/> to mint that first account, staff-side.
///
/// <para>A brand-new merchant also has zero <em>roles</em> (roles are equally self-service), so minting an
/// account with no role would log in to a portal where every action is blocked. <see cref="ProvisionFirstPortalAccountAsync"/>
/// therefore also auto-creates one "Admin" role, granted the wildcard permission (mirrors the platform staff
/// Admin role's own full-access convention) — the merchant's own admin can create narrower roles afterward,
/// entirely self-service from there.</para>
/// </summary>
public static class OpsMerchantPortalAccountEndpoints
{
    /// <summary>The one-time bootstrap role name. Reused (not re-created) on a retry — see the "already
    /// provisioned" guard below.</summary>
    private const string DefaultAdminRoleName = "Admin";

    /// <summary>Mirrors <c>MerchantRole.WildcardPermission</c> / <c>PortalPermissions.PortalWildcard</c> — kept
    /// as its own constant rather than referencing either: the wildcard string is owned by the portal host
    /// (§4.5, permission codes are host vocabulary), and this host must not reference another HOST's types.</summary>
    private const string PortalWildcard = "*";

    private static readonly Error PortalAccountAlreadyExists = Error.Conflict(
        "merchant.portal_account_exists",
        "This merchant already has a portal account. Use the portal's own account management (or a password " +
        "reset) instead of provisioning a second bootstrap account.");

    public static void MapOpsMerchantPortalAccountApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/merchants/{id:guid}/portal-account", CreatePortalAccountAsync)
            .RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapGet("/api/v1/ops/merchants/{id:guid}/portal-accounts", ListPortalAccountsAsync)
            .RequirePermission(OpsPermissions.Merchants.Manage);
        app.MapPost("/api/v1/ops/merchants/{id:guid}/portal-accounts/{accountId:guid}/reset-password", ResetPortalAccountPasswordAsync)
            .RequirePermission(OpsPermissions.Merchants.Manage);
    }

    /// <summary>Staff need this before a reset: a merchant can have more than one portal account (its own
    /// admin may have added teammates), so staff must see which one to reset rather than guess.</summary>
    private static async Task<IResult> ListPortalAccountsAsync(Guid id, IMerchantAccountService accounts, HttpContext http)
    {
        var result = await accounts.ListAsync(id, http.RequestAborted);
        return result.IsFailure
            ? OpsResults.Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = new { merchantId = id, accounts = result.Value }, error = (string?)null, errorCode = (string?)null });
    }

    /// <summary>Staff-triggered reset — for when a merchant's admin is locked out and has nobody else to reset
    /// it from inside the portal itself. Invalidates the old password immediately; the new one-time password
    /// is shown exactly once, here, same as account creation.</summary>
    private static async Task<IResult> ResetPortalAccountPasswordAsync(
        Guid id, Guid accountId, IMerchantAccountService accounts, IAuditLogger audit, HttpContext http)
    {
        var result = await accounts.ResetPasswordAsync(id, accountId, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        // That a reset happened, and by which staff member — never the issued password itself (§10).
        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.portal_account_password_reset", "Merchant", id.ToString(),
            $"username={result.Value.Username}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = id,
                merchantUserId = result.Value.MerchantUserId,
                username = result.Value.Username,
                temporaryPassword = result.Value.TemporaryPassword, // shown once
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    private static async Task<IResult> CreatePortalAccountAsync(
        Guid id, IMerchantRegistrar registrar, IMerchantAccountService accounts, IMerchantRoleService roles,
        IAuditLogger audit, HttpContext http)
    {
        var merchant = await registrar.GetAsync(id, http.RequestAborted);
        if (merchant.IsFailure)
            return OpsResults.Fail(merchant.Error!);

        var result = await ProvisionFirstPortalAccountAsync(
            id, merchant.Value.MerchantCode, merchant.Value.Name, accounts, roles, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        // The username is recorded; the generated password NEVER is (§10 — a credential must not be
        // recoverable from an audit trail, which is read by more people than issued it).
        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "merchant.portal_account_created", "Merchant", id.ToString(),
            $"username={result.Value.Username}", actor.IpAddress), http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                merchantId = id,
                username = result.Value.Username,
                // Shown ONCE. The UI must present this as copy-now-never-again, same as apiSecret/signingSecret.
                temporaryPassword = result.Value.TemporaryPassword,
            },
            error = (string?)null, errorCode = (string?)null,
        });
    }

    /// <summary>
    /// The shared provisioning step — called here for an existing merchant, and from <c>OpsMerchantEndpoints</c>
    /// right after a merchant is created (same effect, one code path). Refuses if the merchant already has a
    /// portal account (never silently mints a redundant "admin"); otherwise ensures one wildcard-permission
    /// "Admin" role exists for the tenant, then creates the account against it. The username is always the
    /// merchant's own code, lowercased — already globally unique and character-safe by construction, so no
    /// separate collision handling is needed here (a genuine collision surfaces as the same
    /// "username already exists" failure <see cref="IMerchantAccountService.CreateAsync"/> already returns).
    /// </summary>
    public static async Task<Result<MerchantAccountCredential>> ProvisionFirstPortalAccountAsync(
        Guid merchantId, string merchantCode, string merchantName,
        IMerchantAccountService accounts, IMerchantRoleService roles, CancellationToken cancellationToken)
    {
        var existingAccounts = await accounts.ListAsync(merchantId, cancellationToken);
        if (existingAccounts.IsFailure)
            return Result.Failure<MerchantAccountCredential>(existingAccounts.Error!);
        if (existingAccounts.Value.Count > 0)
            return Result.Failure<MerchantAccountCredential>(PortalAccountAlreadyExists);

        var existingRoles = await roles.ListAsync(merchantId, cancellationToken);
        if (existingRoles.IsFailure)
            return Result.Failure<MerchantAccountCredential>(existingRoles.Error!);

        var adminRoleId = existingRoles.Value
            .FirstOrDefault(r => string.Equals(r.Name, DefaultAdminRoleName, StringComparison.OrdinalIgnoreCase))
            ?.RoleId;

        if (adminRoleId is null)
        {
            var createdRole = await roles.CreateAsync(
                merchantId, DefaultAdminRoleName, "Full access — auto-created for the merchant's first portal login.",
                [PortalWildcard], cancellationToken);
            if (createdRole.IsFailure)
                return Result.Failure<MerchantAccountCredential>(createdRole.Error!);

            adminRoleId = createdRole.Value.RoleId;
        }

        var username = merchantCode.Trim().ToLowerInvariant();
        return await accounts.CreateAsync(merchantId, username, merchantName, adminRoleId, cancellationToken);
    }
}
