using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.PaymentIntent.Contracts;

namespace CryptoPaymentEngine.Api.OperationsApi.Endpoints;

/// <summary>
/// Gives each ledger row the <c>systemOrderNumber</c> (系统流水号) staff see on the deposit and withdrawal screens,
/// so the two can be cross-checked — shared by <c>GET /transactions</c> and <c>GET /merchants/{id}/balance-history</c>.
///
/// <para><b>Why <c>referenceId</c> alone didn't line up for deposits.</b> A ledger journal's <c>referenceId</c> is
/// the record that CAUSED it, and is also the ledger's duplicate-protection key — so it is never rewritten (the
/// ledger is append-only, §14). For withdrawals that record IS the order (its id is the withdrawal screen's
/// <c>systemOrderNumber</c>). For deposits it is the on-chain DEPOSIT, a different record from the ORDER (invoice)
/// the deposit screen shows — one order is paid by a deposit, but a deposit can also arrive with no order. So the
/// order number is looked up here, at read time — which also covers every historical row with no backfill.</para>
/// </summary>
internal static class LedgerOrderNumbers
{
    /// <summary>Journals caused by a deposit: <c>referenceId</c> is the deposit id → look up the order it paid.</summary>
    private static readonly HashSet<string> DepositTypes = new(StringComparer.Ordinal)
    {
        "Deposit", "DepositReversal", "MerchantTopUp", "MerchantTopUpReversal",
    };

    /// <summary>Journals caused by a withdrawal / payout / cash-out: <c>referenceId</c> already IS the order number.</summary>
    private static readonly HashSet<string> WithdrawalTypes = new(StringComparer.Ordinal)
    {
        "Withdrawal", "WithdrawalReserve", "WithdrawalSettle", "WithdrawalRelease",
    };

    /// <summary>Resolves the order number for every row of a page in ONE query. Rows with no order (a manual
    /// balance adjustment, a hot-wallet top-up, gas, a deposit that arrived with no order) get null.</summary>
    public static async Task<Func<string, Guid, Guid?>> ResolveAsync(
        IEnumerable<(string ReferenceType, Guid ReferenceId)> rows,
        IPaymentIntentDirectory paymentIntents,
        CancellationToken cancellationToken)
    {
        var list = rows.ToList();
        var depositIds = list.Where(r => DepositTypes.Contains(r.ReferenceType)).Select(r => r.ReferenceId).Distinct().ToList();
        var orders = await paymentIntents.FindPublicReferencesByDepositIdsAsync(depositIds, cancellationToken);

        return (referenceType, referenceId) =>
            WithdrawalTypes.Contains(referenceType) ? referenceId
            : DepositTypes.Contains(referenceType) && orders.TryGetValue(referenceId, out var order) ? order
            : null;
    }
}
