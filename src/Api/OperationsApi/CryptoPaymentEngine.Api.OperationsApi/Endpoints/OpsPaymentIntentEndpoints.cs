using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Application;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>Staff-facing payment-intent management — currently just the manual fail (§ ops).</summary>
public static class OpsPaymentIntentEndpoints
{
    public static void MapOpsPaymentIntentApi(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/ops/payment-intents/{reference:guid}/fail", FailAsync).RequireAdmin();

    private static async Task<IResult> FailAsync(
        Guid reference, FailPaymentIntentRequest request, IPaymentIntentAdminService admin, HttpContext http)
    {
        var result = await admin.FailAsync(new FailPaymentIntentCommand(reference, request.Reason), http.RequestAborted);

        if (result.IsFailure)
        {
            var status = result.Error!.Type == ErrorType.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return Results.Json(new { isSuccess = false, error = result.Error!.Message }, statusCode: status);
        }

        return Results.Ok(new { isSuccess = true, data = new { reference, status = "failed" }, error = (string?)null });
    }
}
