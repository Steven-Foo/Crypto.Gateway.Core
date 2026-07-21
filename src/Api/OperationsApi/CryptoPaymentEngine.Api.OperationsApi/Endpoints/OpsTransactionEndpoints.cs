using CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Staff-facing transaction history — read straight from the immutable ledger, not from
/// Deposit/Withdrawal/PaymentIntent's own tables (§ ILedgerQuery.GetJournalsAsync).
///
/// <c>transactionId</c> search resolves through PaymentIntent only (deposit-side) — this host doesn't
/// compose the Withdrawal module yet, so withdrawal-side resolution (by <c>Withdrawal.IdempotencyKey</c>)
/// isn't wired in. Add it the same way once Ops needs to search withdrawal transactions by ID.
/// </summary>
public static class OpsTransactionEndpoints
{
    public static void MapOpsTransactionApi(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/ops/transactions", ListAsync);

    private static async Task<IResult> ListAsync(
        ILedgerQuery ledger,
        IPaymentIntentDirectory paymentIntents,
        HttpContext http,
        Guid? merchantId = null,
        string? transactionId = null,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        // Resolving a merchant's own transaction string only makes sense scoped to that merchant — both
        // PaymentIntent's and (eventually) Withdrawal's idempotency keys are unique per-merchant, not globally.
        Guid? referenceId = null;
        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            if (merchantId is null)
                return Results.Json(
                    new { isSuccess = false, error = "merchantId is required when filtering by transactionId." },
                    statusCode: StatusCodes.Status400BadRequest);

            referenceId = await paymentIntents.FindMatchedDepositIdAsync(merchantId.Value, transactionId, http.RequestAborted);
            if (referenceId is null)
                return Results.Ok(new { isSuccess = true, data = new { page, pageSize, totalCount = 0, items = Array.Empty<object>() }, error = (string?)null });
        }

        var (items, total) = await ledger.GetJournalsAsync(merchantId, referenceId, fromDate, toDate, page, pageSize, http.RequestAborted);

        return Results.Ok(new
        {
            isSuccess = true,
            data = new
            {
                page,
                pageSize,
                totalCount = total,
                items = items.Select(i => new
                {
                    journalId = i.JournalId,
                    referenceType = i.ReferenceType,
                    referenceId = i.ReferenceId,
                    assetId = i.AssetId,
                    description = i.Description,
                    direction = i.Direction,
                    amount = i.Amount.ToString(),
                    createdAt = i.CreatedAt,
                }),
            },
            error = (string?)null,
        });
    }
}
