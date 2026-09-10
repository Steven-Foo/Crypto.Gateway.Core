using CryptoPaymentEngine.Api.MerchantPortalApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

namespace CryptoPaymentEngine.Api.MerchantPortalApi.Endpoints;

/// <summary>
/// The merchant's own administrative activity log — who did what in this portal, and when. Answers "who gave
/// that user the ability to approve payouts?" and "when was our API credential rotated?", which previously had
/// no answer at all on the merchant side.
///
/// <para><b>Tenant isolation is structural here, not a filter.</b> The scope is built from the session's
/// merchant id and passed as <see cref="AuditScope.Merchant"/>, so the query can only return rows stamped with
/// this tenant. Platform-staff entries (which carry no tenant) and other merchants' entries are excluded by
/// the same condition — there is no request parameter that could widen it.</para>
///
/// <para>Read-only by construction: entries are written by the endpoints that perform actions, and nothing
/// exposes a way to edit or delete one. An audit trail a tenant could rewrite would be worthless as evidence.</para>
/// </summary>
public static class PortalActivityEndpoints
{
    public static void MapPortalActivityApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/portal/activity", ListAsync).RequirePortalPermission(PortalPermissions.Activity.View);

    private static async Task<IResult> ListAsync(
        IAuditQuery audit,
        HttpContext http,
        string? action = null,
        string? entityType = null,
        string? entityId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        pageSize = pageSize switch { < 1 => 50, > 200 => 200, _ => pageSize };

        // StaffUserId is deliberately NOT exposed as a filter: on this host the acting user is a merchant-portal
        // user, and letting a caller pass an arbitrary id invites probing. Filter client-side by the returned
        // actor if needed.
        var filter = new AuditSearchFilter(
            StaffUserId: null, Action: action, EntityType: entityType, EntityId: entityId,
            FromDate: fromDate, ToDate: toDate);

        var scope = new AuditScope.Merchant(PortalTenant.MerchantId(http));
        var (items, total) = await audit.SearchAsync(scope, filter, page, pageSize, http.RequestAborted);

        var rows = items.Select(e => new
        {
            id = e.Id,
            // Who acted, as recorded at the time — a later rename or deletion of the account does not rewrite
            // history.
            actor = e.StaffUsername,
            actorUserId = e.StaffUserId,
            action = e.Action,
            entityType = e.EntityType,
            entityId = e.EntityId,
            detail = e.Reason,
            ipAddress = e.IpAddress,
            createdAt = e.CreatedAt,
        });

        return Results.Ok(new
        {
            isSuccess = true,
            data = new { page, pageSize, totalCount = total, items = rows },
            error = (string?)null,
            errorCode = (string?)null,
        });
    }
}
