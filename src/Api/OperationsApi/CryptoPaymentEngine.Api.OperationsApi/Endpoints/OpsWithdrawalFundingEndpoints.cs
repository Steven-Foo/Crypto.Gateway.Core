using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Identity.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing actions on withdrawals parked by the physical hot-wallet float gate: <b>release</b> an
/// above-threshold payout for sending (shown as <c>status: "awaiting_release"</c>) and <b>cancel</b> a payout
/// that cannot be funded (shown as <c>status: "insufficient_balance"</c>). Backed by
/// <c>IWithdrawalFundingService</c>. A parked-for-funds withdrawal needs no endpoint to <em>resume</em> — the
/// processing worker self-resumes it once the hot wallet is reloaded from treasury; these are only the human
/// decisions (release a large one, or give up on one).
/// </summary>
public static class OpsWithdrawalFundingEndpoints
{
    public static void MapOpsWithdrawalFundingApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/release", ReleaseAsync).RequireAdmin();
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/cancel", CancelAsync).RequireAdmin();
    }

    private static async Task<IResult> ReleaseAsync(
        Guid withdrawalId, IWithdrawalFundingService funding, HttpContext http)
    {
        var result = await funding.ReleaseAsync(withdrawalId, OperatorId(http), http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = new { withdrawalId, status = "Released" }, error = (string?)null });
    }

    private static async Task<IResult> CancelAsync(
        Guid withdrawalId, CancelWithdrawalRequest request, IWithdrawalFundingService funding, HttpContext http)
    {
        var result = await funding.CancelAsync(withdrawalId, OperatorId(http), request.Reason, http.RequestAborted);
        return result.IsFailure
            ? Fail(result.Error!)
            : Results.Ok(new { isSuccess = true, data = new { withdrawalId, status = "Cancelled" }, error = (string?)null });
    }

    /// <summary>The authenticated staff caller's id — never a request field, so no one can act as someone else.</summary>
    private static string OperatorId(HttpContext http) =>
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
