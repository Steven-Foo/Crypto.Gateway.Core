using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing approve/reject for withdrawals stuck in <c>PendingApproval</c> (shown as
/// <c>status: "pending_approval"</c> on the withdrawal transaction-search screen). Backed by the
/// pre-existing <c>IWithdrawalApprovalService</c> — this is the endpoint that was missing, not new business
/// logic. Reject raises <c>WithdrawalFailed</c>, which releases the reserved funds and — now that the
/// withdrawal callback handlers exist — notifies the merchant the same way an automatic failure would.
/// </summary>
public static class OpsWithdrawalApprovalEndpoints
{
    public static void MapOpsWithdrawalApprovalApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/approve", ApproveAsync).RequirePermission(OpsPermissions.Withdrawals.Approve);
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/reject", RejectAsync).RequirePermission(OpsPermissions.Withdrawals.Approve);
    }

    private static async Task<IResult> ApproveAsync(
        Guid withdrawalId, IWithdrawalApprovalService approvals, IAuditLogger audit, HttpContext http)
    {
        var actor = AuditActor.From(http);
        var result = await approvals.ApproveAsync(withdrawalId, actor.StaffUserId.ToString(), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "withdrawal.approved", "Withdrawal", withdrawalId.ToString(), null, actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { withdrawalId, status = "Approved" }, error = (string?)null });
    }

    private static async Task<IResult> RejectAsync(
        Guid withdrawalId, RejectWithdrawalRequest request, IWithdrawalApprovalService approvals, IAuditLogger audit, HttpContext http)
    {
        var actor = AuditActor.From(http);
        var result = await approvals.RejectAsync(withdrawalId, actor.StaffUserId.ToString(), request.Reason, http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "withdrawal.rejected", "Withdrawal", withdrawalId.ToString(), request.Reason, actor.IpAddress),
            http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { withdrawalId, status = "Rejected" }, error = (string?)null });
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
