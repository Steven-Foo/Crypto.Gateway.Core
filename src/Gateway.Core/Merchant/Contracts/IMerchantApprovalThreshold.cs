using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.Merchant.Contracts;

/// <summary>
/// The read seam the Withdrawal module consumes to apply a merchant's own approval-threshold override on top of
/// the platform config default (§4.5). The threshold is the payout amount above which a withdrawal — a user
/// payout OR a merchant cash-out — needs human oversight (approve at request + release at processing, §10).
/// Returns <c>null</c> when the merchant has not set one for the asset ⇒ the flow uses the platform config
/// threshold. Distinct from the user-only <see cref="IMerchantWithdrawalLimits"/> and the cash-out
/// <see cref="IMerchantWithdrawalCap"/> — this applies to both withdrawal kinds.
/// </summary>
public interface IMerchantApprovalThreshold
{
    Task<BigInteger?> GetAsync(Guid merchantId, Guid assetId, CancellationToken cancellationToken = default);
}
