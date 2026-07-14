using System.Numerics;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.Financial.Ledger.Contracts;

/// <summary>What the Withdrawal module asks the Ledger to lock at request time. Amounts are base units.</summary>
public sealed record ReserveWithdrawalRequest(Guid WithdrawalId, Guid MerchantId, Guid AssetId, BigInteger Amount, BigInteger Fee);

/// <summary>
/// The Ledger's public, synchronous reserve entry point for the Withdrawal module. Reserving debits the
/// merchant's liability (and holds it in a clearing account) in one atomic posting; the ledger's
/// balance-can't-go-negative guard <em>is</em> the sufficiency check, so this is how a withdrawal request
/// learns immediately whether the merchant can afford it — and how two concurrent requests can't both
/// drain the same balance. Settlement/release happen later, event-driven (§7.5).
///
/// This is a cross-module call through a Contracts interface — the same pattern Wallet uses to reach
/// Merchant/KeyManagement (§4.5) — not a call into the Ledger's internals.
/// </summary>
public interface IWithdrawalLedger
{
    /// <summary>
    /// Locks the funds. Success = reserved (or already reserved on replay). Failure with
    /// <c>ledger.insufficient_balance</c> = the merchant cannot afford it; the request must be rejected.
    /// Idempotent on the withdrawal id.
    /// </summary>
    Task<Result> ReserveAsync(ReserveWithdrawalRequest request, CancellationToken cancellationToken = default);
}
