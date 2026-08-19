using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Notification.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing manual callback resend — the escape hatch for a callback automatic retries gave up on
/// (§ CallbackDeliveryStatus.Abandoned). Resends the exact persisted, already-signed payload; never re-signs,
/// never re-builds. <c>{type}</c> is <c>"deposit"</c> or <c>"withdrawal"</c>, <c>{referenceId}</c> the same
/// id shown as <c>systemOrderNumber</c> on the transaction-search screens.
/// </summary>
public static class OpsCallbackEndpoints
{
    public static void MapOpsCallbackApi(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/ops/callbacks/{type}/{referenceId:guid}/resend", ResendAsync).RequirePermission(OpsPermissions.Callbacks.Manage);

    private static async Task<IResult> ResendAsync(
        string type, Guid referenceId, ICallbackDeliveryResendService resend, IAuditLogger audit, HttpContext http)
    {
        if (!TryParseReferenceType(type, out var referenceType))
            return Results.Json(
                new { isSuccess = false, error = "type must be 'deposit' or 'withdrawal'." },
                statusCode: StatusCodes.Status400BadRequest);

        var result = await resend.ResendAsync(referenceType, referenceId, http.RequestAborted);
        if (result.IsFailure)
        {
            var status = result.Error!.Type == ErrorType.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: status);
        }

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "callback.resent", "Callback", referenceId.ToString(), type, actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { type, referenceId }, error = (string?)null });
    }

    private static bool TryParseReferenceType(string type, out CallbackReferenceType referenceType)
    {
        switch (type.Trim().ToLowerInvariant())
        {
            case "deposit":
                referenceType = CallbackReferenceType.Deposit;
                return true;
            case "withdrawal":
                referenceType = CallbackReferenceType.Withdrawal;
                return true;
            default:
                referenceType = default;
                return false;
        }
    }
}
