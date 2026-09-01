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
/// One event that actually moved a merchant's <c>MerchantLiability</c> balance — the "account statement"
/// view, distinct from <see cref="MerchantJournalView"/>/<see cref="ILedgerQuery.GetJournalsAsync"/>: a
/// <c>WithdrawalSettle</c> journal carries the merchant's id for reporting but never touches their liability
/// line (the balance already moved at reserve time; settle only relocates <c>WithdrawalClearing</c> →
/// <c>TreasuryAsset</c>/<c>FeeRevenue</c>) — that journal is deliberately excluded here, never shown as a
/// zero-amount row. Every row returned genuinely changed <see cref="Amount"/> in the direction of
/// <see cref="Direction"/>.
/// </summary>
public sealed record MerchantBalanceChangeView(
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
    /// The merchant's <em>settled</em> (withdrawable) balance for one asset, in base units — the available
    /// balance minus the deposit inflow that has not yet matured past the merchant's settlement period (T+N).
    /// A deposit whose journal is dated on/after <paramref name="unmaturedCutoffUtc"/> is still maturing and is
    /// excluded; everything older is settled and withdrawable. Computed as the authoritative cache balance
    /// minus the net (credit − debit) of the merchant's liability line across <c>Deposit</c>/<c>DepositReversal</c>
    /// journals in the unmatured window — so releases, reversals and fees already folded into the cache stay
    /// correct, and a deposit plus its reorg-reversal (both recent) net to zero. Clamped at zero; never negative,
    /// and always ≤ <see cref="GetMerchantBalanceAsync"/> for the same merchant/asset.
    /// </summary>
    Task<BigInteger> GetMerchantSettledBalanceAsync(
        Guid merchantId, Guid assetId, DateTimeOffset unmaturedCutoffUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// The ledger's total on-chain holding for one asset, in base units — the balance of the platform's
    /// <c>TreasuryAsset</c> account. This is the ledger's <em>claim</em> of how much of the asset it custodies
    /// across every address it controls: it rises by the gross of every confirmed deposit and falls by every
    /// settled withdrawal (§14, derived from the immutable journal, never a stored mutable number). The
    /// Reconciliation module compares this against the summed on-chain balance of the controlled addresses to
    /// detect custody drift — it is the only ledger-derivable custody figure, because the ledger tracks
    /// accounting buckets, never addresses (§8). Returns zero when the asset has never been custodied.
    /// </summary>
    Task<BigInteger> GetTreasuryHoldingAsync(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Total company funds contributed into the hot withdrawal pool for an asset — the running balance of the
    /// <c>WithdrawalWalletTopUp</c> account. Part of the custody in <see cref="GetTreasuryHoldingAsync"/>, but
    /// operating float rather than merchant money, so the custody screen can show the two apart.
    ///
    /// <para>Exposed as its own named figure rather than a generic "read account X" so the ledger's internal
    /// chart of accounts stays inside the module (§4.5) — a caller asks a business question, not for a row.
    /// Returns zero when nothing has been contributed for this asset.</para>
    /// </summary>
    Task<BigInteger> GetWithdrawalWalletTopUpTotalAsync(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Total merchant settlements paid from company wallets OUTSIDE platform custody — the running balance of
    /// the <c>ExternalSettlement</c> account. Deliberately NOT part of custody or of earnings: it is company
    /// money spent discharging merchant obligations, so counting it as either would misstate the position.
    /// Returns zero when nothing has been settled externally for this asset.
    /// </summary>
    Task<BigInteger> GetExternalSettlementTotalAsync(Guid assetId, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// One merchant's balance history ("account statement") — newest first, every entry that actually
    /// credited or debited their <c>MerchantLiability</c> balance (real deposits/reversals, withdrawal
    /// reserves/releases, manual credits/debits), and nothing that didn't (see
    /// <see cref="MerchantBalanceChangeView"/> for why <c>WithdrawalSettle</c> is excluded). All filters
    /// beyond <paramref name="merchantId"/> are optional and combine with AND.
    /// </summary>
    Task<(IReadOnlyList<MerchantBalanceChangeView> Items, int TotalCount)> GetMerchantBalanceHistoryAsync(
        Guid merchantId,
        Guid? assetId,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
