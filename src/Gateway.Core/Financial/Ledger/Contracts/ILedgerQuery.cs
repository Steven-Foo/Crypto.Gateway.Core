using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;

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
}
