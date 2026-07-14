using System.Numerics;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;

/// <summary>
/// Per-asset withdrawal policy (all base units). <see cref="Fee"/> is charged on top (§ fee model):
/// the merchant's balance is debited amount+fee and the destination receives the full amount.
/// <see cref="ApprovalThreshold"/> is the amount above which a human must approve (§10, hot/cold).
/// A per-merchant override is a future refinement; today the policy is platform-wide per asset.
/// </summary>
public sealed record WithdrawalPolicy(BigInteger Minimum, BigInteger? Maximum, BigInteger Fee, BigInteger ApprovalThreshold, int Confirmations)
{
    public bool IsBelowMinimum(BigInteger amount) => amount < Minimum;

    public bool ExceedsMaximum(BigInteger amount) => Maximum is not null && amount > Maximum.Value;

    public bool RequiresApproval(BigInteger amount) => amount > ApprovalThreshold;
}
