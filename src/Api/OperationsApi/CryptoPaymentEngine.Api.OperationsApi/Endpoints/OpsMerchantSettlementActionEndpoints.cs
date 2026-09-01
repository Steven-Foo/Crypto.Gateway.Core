using CryptoPaymentEngine.Api.OperationsApi.Models;
using CryptoPaymentEngine.Api.OperationsApi.Security;
using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application;
using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// The merchant-settlement (cash-out) workflow. This system does <b>not</b> pay a merchant settlement: an
/// admin audits the request, a finance admin pays the merchant from a company wallet OUTSIDE platform
/// custody, and the transaction is recorded here.
///
/// <para>Three actions, matching the three human decisions: <b>audit-approve</b> (the request is legitimate,
/// pay it), <b>audit-reject</b> (it is not — the merchant's reserved funds go back), and
/// <b>record-settlement</b> (it has been paid; here is the hash).</para>
///
/// <para>The recorded hash is <b>verified on-chain before anything is written</b> — it must exist, be
/// confirmed, and carry the right destination, asset and amount. That check is what separates a settled fact
/// from an operator's claim: without it, a typo would discharge a merchant's balance against a payment that
/// never happened. A failed verification changes nothing and returns the specific reason, so the operator can
/// tell "still confirming" from "wrong hash" from "wrong amount".</para>
/// </summary>
public static class OpsMerchantSettlementActionEndpoints
{
    public static void MapOpsMerchantSettlementActionApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/audit-approve", AuditApproveAsync)
            .RequirePermission(OpsPermissions.Withdrawals.Approve);

        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/audit-reject", AuditRejectAsync)
            .RequirePermission(OpsPermissions.Withdrawals.Approve);

        // Recording the payment is a FINANCE action, not an approval one — deliberately gated on the manage
        // code rather than approve, so the person who signs a settlement off need not be the one who can
        // declare it paid.
        app.MapPost("/api/v1/ops/withdrawals/{withdrawalId:guid}/record-settlement", RecordSettlementAsync)
            .RequirePermission(OpsPermissions.Withdrawals.Manage);
    }

    private static async Task<IResult> AuditApproveAsync(
        Guid withdrawalId, IMerchantSettlementService settlements, IAuditLogger audit, HttpContext http)
    {
        var actor = AuditActor.From(http);
        var result = await settlements.AuditApproveAsync(withdrawalId, actor.Username, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "settlement.audit_approved", "Withdrawal", withdrawalId.ToString(),
            null, actor.IpAddress), http.RequestAborted);

        return OpsResults.Ok(new { withdrawalId, status = result.Value.Status });
    }

    private static async Task<IResult> AuditRejectAsync(
        Guid withdrawalId, RejectSettlementRequest request, IMerchantSettlementService settlements,
        IAuditLogger audit, HttpContext http)
    {
        var actor = AuditActor.From(http);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Rejected at audit." : request.Reason.Trim();

        var result = await settlements.AuditRejectAsync(withdrawalId, actor.Username, reason, http.RequestAborted);
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "settlement.audit_rejected", "Withdrawal", withdrawalId.ToString(),
            reason, actor.IpAddress), http.RequestAborted);

        return OpsResults.Ok(new { withdrawalId, status = result.Value.Status });
    }

    private static async Task<IResult> RecordSettlementAsync(
        Guid withdrawalId, RecordSettlementRequest request, IMerchantSettlementService settlements,
        IAuditLogger audit, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionHash))
            return OpsResults.Bad(OpsErrorCodes.InvalidHex, "transactionHash is required.");

        var actor = AuditActor.From(http);
        var result = await settlements.RecordSettlementAsync(
            withdrawalId, actor.Username, request.TransactionHash.Trim(), request.SourceAddress?.Trim(),
            http.RequestAborted);

        // A verification failure is a business outcome, not an error: the operator sees exactly which check
        // failed and can correct the hash, or wait, and try again. Nothing was written.
        if (result.IsFailure)
            return OpsResults.Fail(result.Error!);

        await audit.LogAsync(new LogAuditEntryCommand(
            actor.StaffUserId, actor.Username, "settlement.recorded", "Withdrawal", withdrawalId.ToString(),
            request.TransactionHash.Trim(), actor.IpAddress), http.RequestAborted);

        return OpsResults.Ok(new { withdrawalId, status = result.Value.Status, transactionHash = request.TransactionHash.Trim() });
    }
}
