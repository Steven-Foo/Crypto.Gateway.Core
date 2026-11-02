using CryptoPaymentEngine.Gateway.Core.Platform.Audit.Application;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Who made each manual balance adjustment on a page of ledger rows (操作人) — shared by <c>GET /transactions</c>
/// and <c>GET /merchants/{id}/balance/history</c>.
///
/// <para>The link: a manual credit/debit's adjustment id is BOTH the ledger journal's <c>referenceId</c> and the
/// audit entry's entity id (<see cref="AdjustmentEntityType"/>), so one indexed audit query resolves a whole page.
/// The ledger itself records no operator — who did it is an audit fact, kept in the audit log.</para>
///
/// <para><b>Only adjustments made after 2026-10-30 resolve.</b> Earlier audit entries were logged against the
/// merchant, not the adjustment, so they can't be matched to one row; those rows show null (the audit log still
/// has them).</para>
/// </summary>
internal static class LedgerOperators
{
    /// <summary>The audit entity type a manual balance adjustment is logged under; its entity id is the adjustment id.</summary>
    public const string AdjustmentEntityType = "MerchantBalanceAdjustment";

    public static async Task<Func<string, Guid, string?>> ResolveAsync(
        IEnumerable<(string ReferenceType, Guid ReferenceId)> rows, IAuditQuery audit, CancellationToken cancellationToken)
    {
        var adjustmentIds = rows
            .Where(r => r.ReferenceType == "Adjustment")
            .Select(r => r.ReferenceId.ToString())
            .Distinct()
            .ToList();

        var actors = await audit.FindActorsAsync(AdjustmentEntityType, adjustmentIds, cancellationToken);

        return (referenceType, referenceId) =>
            referenceType == "Adjustment" && actors.TryGetValue(referenceId.ToString(), out var who) ? who : null;
    }
}
