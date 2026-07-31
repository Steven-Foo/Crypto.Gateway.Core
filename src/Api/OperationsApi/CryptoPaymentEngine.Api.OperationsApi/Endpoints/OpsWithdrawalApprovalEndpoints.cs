using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
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
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/approve", ApproveAsync).RequireAdmin();
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/reject", RejectAsync).RequireAdmin();
    }

    private static async Task<IResult> ApproveAsync(
        Guid withdrawalId, IWithdrawalApprovalService approvals, HttpContext http)
    {
        var result = await approvals.ApproveAsync(withdrawalId, ApproverId(http), http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = new { withdrawalId, status = "Approved" }, error = (string?)null });
    }

    private static async Task<IResult> RejectAsync(
        Guid withdrawalId, RejectWithdrawalRequest request, IWithdrawalApprovalService approvals, HttpContext http)
    {
        var result = await approvals.RejectAsync(withdrawalId, ApproverId(http), request.Reason, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = new { withdrawalId, status = "Rejected" }, error = (string?)null });
    }

    /// <summary>The authenticated staff caller's id — never a request field, so no one can approve as
    /// someone else. <c>StaffPrincipal</c> carries no display name today, only the id.</summary>
    private static string ApproverId(HttpContext http) =>
        ((StaffPrincipal)http.Items[StaffBearerAuthMiddleware.PrincipalItem]!).StaffUserId.ToString();

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
