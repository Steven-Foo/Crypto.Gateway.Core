using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Deposit.Contracts;

/// <summary>
/// A read-only signal for UX composition only — never the money path. Lets a caller (the hosted pay page)
/// show "payment seen, confirming on-chain" the moment the scanner detects a transfer, well before it
/// reaches the credit threshold. The Ledger credit and merchant webhook still wait for the full
/// <c>DepositConfirmed</c> event; this never influences that decision (§4.5, §9).
/// </summary>
/// <summary>The Ops-facing sliver of a matched deposit — just what the transaction-search screen needs to
/// enrich a <c>PaymentIntentAdminRow</c>. <see cref="AmountBaseUnits"/> is the exact on-chain amount actually
/// received (may differ from the invoice's expected amount — see <c>PaymentIntent.AmountMatched</c>);
/// <see cref="FeeBaseUnits"/> is the platform fee charged on it, snapshotted at detection (§14).
/// <see cref="TransactionHash"/> lets staff pull the transaction up on a block explorer directly from the
/// Ops screen.</summary>
public sealed record DepositSummaryView(
    Guid DepositId, string AmountBaseUnits, string FeeBaseUnits, int Confirmations, string TransactionHash);

/// <summary>Aggregate sums over a set of deposits — exact base-unit integer strings (§14).</summary>
public sealed record DepositAmountTotals(string TotalAmountBaseUnits, string TotalFeeBaseUnits);

public interface IDepositLookup
{
    /// <summary>True if an unconfirmed (<c>Detected</c>) deposit currently sits at this address.</summary>
    Task<bool> HasDetectedDepositAsync(Chain chain, string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch-resolves deposits by id — the bridge Ops uses to enrich a page of <c>PaymentIntentAdminRow</c>s
    /// (via <c>MatchedDepositId</c>) without PaymentIntent needing to know Deposit's schema (§4.5). Ids with
    /// no matching row are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DepositSummaryView>> GetByIdsAsync(
        IReadOnlyCollection<Guid> depositIds, CancellationToken cancellationToken = default);

    /// <summary>Sums Amount/Fee across the given deposit ids — the Ops screen's summary totals, resolved
    /// from PaymentIntent's <c>MatchedDepositId</c> list. An empty <paramref name="depositIds"/> sums to
    /// zero, never an error.</summary>
    Task<DepositAmountTotals> SumByIdsAsync(
        IReadOnlyCollection<Guid> depositIds, CancellationToken cancellationToken = default);
}
