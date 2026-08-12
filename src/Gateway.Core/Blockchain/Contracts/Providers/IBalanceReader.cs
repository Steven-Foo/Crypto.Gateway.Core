using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;

/// <summary>
/// A read-only chain capability (§8): the current on-chain balance of one asset at one address, in exact
/// base units (§14). This is OBSERVATION only — it proves what the chain reports right now, it is never a
/// balance the ledger trusts (§8: Blockchain is external state; the Ledger is the only source of truth).
/// The Reconciliation module sums this across every address the platform controls to compare against the
/// ledger's <c>TreasuryAsset</c> holding. Tiny and read-only by design — a reconciler gets no ability to
/// move funds or sign (§10). Implementations are chain-specific adapters (in-memory for dev/test; TRON
/// <c>eth_call balanceOf</c> for staging/prod), selected purely by DI.
/// </summary>
public interface IBalanceReader
{
    /// <summary>
    /// The on-chain balance of <paramref name="assetId"/> held by <paramref name="address"/> on
    /// <paramref name="chain"/>, in base units. An address that has never held the asset reads as zero, not
    /// an error. Throws only on a genuine fault (unknown asset / chain mismatch / node failure) so a caller
    /// can decide whether to skip the address or abort the pass.
    /// </summary>
    Task<BigInteger> GetBalanceAsync(Chain chain, string address, Guid assetId, CancellationToken cancellationToken = default);
}
