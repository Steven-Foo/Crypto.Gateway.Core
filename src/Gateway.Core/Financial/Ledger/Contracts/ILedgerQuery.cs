using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;

/// <summary>
/// One journal's effect on a merchant's own liability account — not the full balanced entry (Treasury/Fee
/// lines are platform-internal, not the merchant's business). <see cref="Direction"/>/<see cref="Amount"/>
/// are the merchant-liability line's own debit-or-credit, so "Credit" reads as money in, "Debit" as money out.
/// </summary>
public sealed record MerchantJournalView(
    Guid JournalId,
    string ReferenceType,
    Guid ReferenceId,
    Guid AssetId,
    string Description,
    string Direction,
    BigInteger Amount,
    DateTimeOffset CreatedAt);

/// <summary>
/// The Ledger module's public, read-only balance projection. A merchant's spendable balance is the
/// balance of its <c>MerchantLiability</c> account for the asset — <em>derived</em> from the immutable
/// journal (via the rebuildable <c>AccountBalance</c> cache), never a stored, mutable number
/// (§14, non-negotiable #4). Funds reserved for an in-flight withdrawal have already left the liability
/// for the clearing account, so this is the <em>available</em> balance, exactly what a merchant may spend.
///
/// This is the one place other modules/hosts read money out of the ledger — a Contracts-only seam
/// (§4.5), never a reach into the Ledger's tables. Amounts are unsigned base units; convert to a
/// display value only at the host edge (§14).
/// </summary>
public interface ILedgerQuery
{
    /// <summary>
    /// The merchant's available balance for one asset, in base units. Returns zero when the merchant
    /// has no ledger activity for that asset yet (no account = nothing owed), never an error.
    /// </summary>
    Task<BigInteger> GetMerchantBalanceAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Journal history, newest first — every event that touched a merchant's liability account (deposit
    /// credits, withdrawal reserve/settle/release, reversals). Read straight from the immutable ledger, not
    /// from Deposit/Withdrawal/PaymentIntent's own tables. All filters are optional and combine with AND:
    /// <paramref name="merchantId"/> null means every merchant (excluding purely platform-internal journals,
    /// which carry no merchant); <paramref name="referenceId"/> is the Ledger's own reference to the
    /// Deposit/Withdrawal/etc. row that caused the journal — resolving a merchant's own transaction string to
    /// this ID is the caller's job (e.g. <c>IPaymentIntentDirectory.FindMatchedDepositIdAsync</c> for a
    /// deposit), since the Ledger must not know those modules exist (§4.5).
    /// </summary>
    Task<(IReadOnlyList<MerchantJournalView> Items, int TotalCount)> GetJournalsAsync(
        Guid? merchantId,
        Guid? referenceId,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
