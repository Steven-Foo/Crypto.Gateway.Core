using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>Staff-facing payment-intent management: manual fail of a stuck-unpaid invoice.</summary>
public static class OpsPaymentIntentEndpoints
{
    public static void MapOpsPaymentIntentApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/payment-intents/{reference:guid}/fail", FailAsync).RequirePermission(OpsPermissions.Deposits.Manage);
    }

    private static async Task<IResult> FailAsync(
        Guid reference, FailPaymentIntentRequest request, IPaymentIntentAdminService admin, IAuditLogger audit, HttpContext http)
    {
        var result = await admin.FailAsync(new FailPaymentIntentCommand(reference, request.Reason), http.RequestAborted);
        if (result.IsFailure)
            return Fail(result.Error!);

        var actor = AuditActor.From(http);
        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "payment_intent.failed", "PaymentIntent", reference.ToString(),
            request.Reason, actor.IpAddress), http.RequestAborted);

        return Results.Ok(new { isSuccess = true, data = new { reference, status = "failed" }, error = (string?)null });
    }

    private static IResult Fail(Error error)
    {
        var status = error.Type == ErrorType.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status409Conflict;
        return Results.Json(new { isSuccess = false, error = error.Message }, statusCode: status);
    }
}
