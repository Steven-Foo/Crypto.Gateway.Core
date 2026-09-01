using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Reconciliation.Application;

/// <summary>The outcome of one asset's reconciliation pass.</summary>
public enum ReconciliationStatus
{
    /// <summary>On-chain total equals the ledger holding (within tolerance). Custody is accounted for.</summary>
    Balanced = 1,

    /// <summary>On-chain total differs from the ledger holding beyond tolerance. Needs investigation.</summary>
    Drift = 2,

    /// <summary>
    /// One or more controlled addresses could not be read, so the on-chain total is partial and the drift is
    /// not trustworthy — neither a clean pass nor a proven discrepancy. Never treated as Balanced (§14: don't
    /// assert correctness we can't prove).
    /// </summary>
    Incomplete = 3,
}

/// <summary>
/// One reconciliation observation for a single asset on a single chain: the ledger's <c>TreasuryAsset</c>
/// holding vs the summed on-chain balance across every address the platform controls. A derived,
/// observability read model (persisted to MongoDB, never money truth — §2). All amounts are exact base
/// units (§14). <see cref="Drift"/> is <c>OnChainTotal − LedgerHolding</c>: positive means more on chain than
/// the ledger records, negative means the ledger records more than is on chain.
/// </summary>
public sealed record ReconciliationSnapshot(
    Chain Chain,
    Guid AssetId,
    string AssetSymbol,
    BigInteger LedgerHolding,
    BigInteger OnChainTotal,
    BigInteger Drift,
    ReconciliationStatus Status,
    int AddressesScanned,
    int AddressesUnreadable,
    DateTimeOffset ObservedAt,
    // ── Where the custody physically sits ───────────────────────────────────────────────────────────────
    // A single TreasuryAsset figure answers "does the ledger match the chain?" but not "match WHERE?", which
    // is the first question an operator asks when it doesn't. These are the same balances already read for
    // the total, grouped by the role of the address holding them — no extra chain reads, and they always sum
    // to OnChainTotal. TreasuryAsset itself stays ONE ledger account: splitting it would fragment the
    // invariant this whole check exists to assert.
    BigInteger ColdTreasuryTotal = default,
    BigInteger HotPoolTotal = default,
    BigInteger DepositAddressTotal = default,
    /// <summary>
    /// Company funds contributed into the hot pool, per the ledger's <c>WithdrawalWalletTopUp</c> account.
    /// Part of <see cref="OnChainTotal"/> but NOT merchant money, so it is shown separately — operating float
    /// read as merchant earnings is exactly the misreading this breakdown exists to prevent.
    ///
    /// <para>It is also the first thing to check against a POSITIVE drift: an admin who has sent a top-up
    /// on-chain but not yet recorded it leaves the chain ahead of the ledger by that amount. Deliberately
    /// reported as a figure to compare rather than an automatic "explained" verdict — the system cannot
    /// distinguish an unrecorded top-up from genuinely unexplained funds, and claiming otherwise would be the
    /// custody screen telling a comfortable story it cannot actually support.</para>
    ///
    /// <para>Note that merchant settlements never appear here or move the drift at all: they are paid from a
    /// wallet outside custody, so no watched address is debited.</para>
    /// </summary>
    BigInteger ToppedUpTotal = default);

/// <summary>
/// Reconciliation policy. <see cref="DriftTolerance"/> is the absolute base-unit drift tolerated before a
/// pass is flagged <see cref="ReconciliationStatus.Drift"/> — default zero (exact match). An operator raises
/// it to absorb known, expected transients (a deposit landed on chain but not yet confirmed into the ledger,
/// or a withdrawal broadcast but not yet settled), which momentarily move the on-chain total off the ledger.
/// It never hides a discrepancy — the exact drift is always recorded on the snapshot regardless of tolerance.
/// </summary>
public sealed class ReconciliationOptions
{
    public BigInteger DriftTolerance { get; init; } = BigInteger.Zero;
}
