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
/// received (may differ from the invoice's expected amount — see <c>PaymentIntent.AmountMatched</c>).</summary>
public sealed record DepositSummaryView(Guid DepositId, string AmountBaseUnits, int Confirmations);

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
}
