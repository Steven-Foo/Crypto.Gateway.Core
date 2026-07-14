using CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Domain;
using CryptoPaymentEngine.SharedKernel;

namespace CryptoPaymentEngine.Gateway.Core.PaymentProcessing.Withdrawal.Application.Abstractions;

/// <summary>Supplies the per-chain <see cref="WithdrawalPolicy"/> (limits, fee, approval threshold, confirmations) from config.</summary>
public interface IWithdrawalPolicyProvider
{
    WithdrawalPolicy For(Chain chain);
}
